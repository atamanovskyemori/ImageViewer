using System.Globalization;
using System.Text.Json;

namespace ImageViewer.Metadata;

/// <summary>
/// ComfyUI が PNG のテキストチャンク "prompt"（WebP などなら EXIF。<see cref="ParseComfyUIExif"/>）に書き込むノード構成（API 形式の JSON）から、生成情報を取り出す。
/// <code>
/// { "3": { "class_type": "KSampler", "inputs": { "seed": 123, "positive": ["6", 0], ... } }, "6": { ... }, ... }
/// </code>
/// 入力の値は、そのまま値が書いてあるか、["ノード番号", 出力の番号] で別のノードの出力につながっている。
/// サンプラーのノードから、つながりをたどって集める：
/// <list type="bullet">
/// <item>positive / negative → CLIPTextEncode などの text（プロンプト）</item>
/// <item>model → CheckpointLoaderSimple などのモデル名（途中の LoraLoader も拾う）</item>
/// <item>latent_image → EmptyLatentImage などの大きさ</item>
/// </list>
/// サンプラーが複数ある（高解像度化など）ときは、空の潜在画像から始めているものを主とする。
/// カスタムノードは書き方がまちまちなので、分かるものだけ拾う。読めないもの・壊れたものは、止まらずに読めた分だけ返す。
/// </summary>
public sealed partial class GenerationInfo
{
    // つながりをたどる深さの上限（壊れた・ループしたノード構成で止まらないように）
    private const int MaxLinkDepth = 32;

    /// <summary>
    /// ComfyUI のノード構成から生成情報を取り出す。サンプラーが無い・読めない JSON なら null。
    /// </summary>
    /// <param name="json">PNG のテキストチャンク "prompt" の中身</param>
    /// <param name="source">どこから読んだか（<see cref="GenerationSources"/> のどれか）</param>
    public static GenerationInfo? ParseComfyUI(string? json, string source = GenerationSources.PngComfyUI)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            return new ComfyGraph(document.RootElement, source).Read(json.Trim());
        }
        catch (JsonException)
        {
            return null; // 壊れた JSON（Python が書く NaN なども含む）。PNG テキストとしてそのまま出す
        }
    }

    /// <summary>
    /// WebP / JPEG で保存したときに EXIF に書かれる、ComfyUI のノード構成から生成情報を取り出す。
    /// 決まった置き場所は無いので、使われている書き方を順に試す：
    /// <list type="bullet">
    /// <item>ComfyUI 本体（SaveAnimatedWEBP）：IFD0 の Model に "prompt:{...}"（Make には "workflow:{...}"）</item>
    /// <item>WAS Node Suite など：Make に "Prompt:{...}"、ImageDescription に "Workflow:{...}"</item>
    /// <item>UserComment などに、"prompt:" を付けずに JSON をそのまま書くもの</item>
    /// </list>
    /// "workflow:" のもの（画面のノード配置。サンプラーの設定は prompt の方から読む）は読まない。見つからなければ null。
    /// </summary>
    public static GenerationInfo? ParseComfyUIExif(ExifData? exif)
    {
        if (exif is null)
        {
            return null;
        }

        string?[] texts =
        [
            exif.GetText(ExifIfd.Primary, ExifTags.Model),
            exif.GetText(ExifIfd.Primary, ExifTags.Make),
            exif.GetText(ExifIfd.Primary, ExifTags.ImageDescription),
            DecodeUserComment(exif.Get(ExifIfd.Exif, ExifTags.UserComment)?.Bytes),
        ];

        foreach (var text in texts)
        {
            if (ComfyUIJson(text) is { Label: not "workflow" } found
                && ParseComfyUI(found.Json, GenerationSources.ExifComfyUI) is { } info)
            {
                return info;
            }
        }
        return null;
    }

    /// <summary>
    /// EXIF の文字列が ComfyUI の JSON（"prompt:{...}" / "workflow:{...}" / "{...}"）か。
    /// メーカー・機種・コメントの欄に、長い JSON をそのまま出さないために使う
    /// </summary>
    public static bool IsComfyUIExifText(string? text) => ComfyUIJson(text) is not null;

    /// <summary>"prompt:" / "workflow:"（大文字小文字は問わない）を外した JSON と、外した名前（小文字。無ければ空）</summary>
    private static (string Label, string Json)? ComfyUIJson(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var body = text.TrimStart();
        string label = "";
        foreach (var prefix in (string[])["prompt", "workflow"])
        {
            if (body.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase))
            {
                label = prefix;
                body = body[(prefix.Length + 1)..].TrimStart();
                break;
            }
        }
        return body.StartsWith('{') ? (label, body) : null;
    }

    /// <summary>ノード構成を読む。JsonDocument が生きている間だけ使う</summary>
    private sealed class ComfyGraph
    {
        // KSampler (Efficient) などは、入力が KSampler と同じ
        private static readonly string[] SamplerTypes =
        [
            "KSampler", "KSamplerAdvanced", "SamplerCustom", "SamplerCustomAdvanced",
            "KSampler (Efficient)", "KSampler Adv. (Efficient)",
        ];

        // 高解像度化のパスをたどるとき（後のサンプラーの latent_image から前のサンプラーまで）に、たどる入力
        // （LatentUpscaleBy の samples、VAEDecode → ImageUpscaleWithModel → VAEEncode の samples / image / pixels など）
        private static readonly string[] ImageChainInputs = ["samples", "latent", "latent_image", "pixels", "image", "images"];

        // 文字列や数値を出すノードの、値の入力の名前（上から順に探す）。
        // ImpactWildcardProcessor のように文字列とシードを両方持つノードで、シードをプロンプトと取り違えないように
        private static readonly string[] ValueInputs =
            ["populated_text", "value", "string", "text", "seed", "noise_seed", "int", "float", "number"];

        // プロンプトの文字列が入っている入力の名前（CLIPTextEncode / SDXL / Flux / ImpactWildcardEncode など）
        private static readonly string[] PromptInputs = ["text", "text_g", "text_l", "t5xxl", "clip_l", "prompt"];

        /// <summary>サンプラーのノードから読んだ設定と、つながりの入口</summary>
        private sealed record SamplerSettings(
            string? Seed, string? Steps, string? Cfg, string? Sampler, string? Scheduler, string? Denoise,
            JsonElement Positive, JsonElement Negative, JsonElement Model, JsonElement LatentImage);

        private readonly Dictionary<string, JsonElement> _nodes = new(StringComparer.Ordinal);

        private readonly string _source;

        public ComfyGraph(JsonElement root, string source)
        {
            _source = source;
            foreach (var node in root.EnumerateObject())
            {
                if (node.Value.ValueKind == JsonValueKind.Object)
                {
                    _nodes[node.Name] = node.Value;
                }
            }
        }

        public GenerationInfo? Read(string raw)
        {
            var samplers = _nodes
                .Where(n => SamplerTypes.Contains(ClassType(n.Value)))
                .OrderBy(n => int.TryParse(n.Key, out int id) ? id : int.MaxValue)
                .ThenBy(n => n.Key, StringComparer.Ordinal)
                .ToList();
            if (samplers.Count == 0)
            {
                return null;
            }

            // 高解像度化などで複数あるときは、ほかのサンプラーの出力を受けていないもの（最初のパス）を主にする。
            // そういうものが複数あれば（別々に生成している構成など）、空の潜在画像から始めているもの、次に番号の小さいもの
            var firstPasses = samplers
                .Where(s => !samplers.Any(other => other.Key != s.Key && PathTo(Input(Inputs(s.Value), "latent_image"), other.Key) is not null))
                .ToList();
            if (firstPasses.Count == 0)
            {
                firstPasses = samplers; // ループしている。番号の小さいものにする
            }
            var main = firstPasses.FirstOrDefault(s => StartsFromEmptyLatent(s.Value));
            if (main.Value.ValueKind == JsonValueKind.Undefined)
            {
                main = firstPasses[0];
            }

            // 高解像度化：主のサンプラーの出力を（拡大などを挟んで）受けているサンプラー。いちばん近いもの
            var hires = samplers
                .Where(s => s.Key != main.Key)
                .Select(s => (Sampler: s.Value, Path: PathTo(Input(Inputs(s.Value), "latent_image"), main.Key)))
                .Where(h => h.Path is not null)
                .OrderBy(h => h.Path!.Count)
                .FirstOrDefault();

            return Build(ReadSettings(main.Value), hires.Path is null ? null : (ReadSettings(hires.Sampler), hires.Path), raw);
        }

        /// <summary>サンプラーのノードから設定を読む。サンプラーの種類で、どの入力に何があるかが違う</summary>
        private SamplerSettings ReadSettings(JsonElement sampler)
        {
            var inputs = Inputs(sampler);
            var latent = Input(inputs, "latent_image");

            switch (ClassType(sampler))
            {
                case "SamplerCustomAdvanced":
                {
                    // noise / guider / sampler / sigmas が、それぞれ別のノードになっている（Flux など）
                    var guiderInputs = Inputs(LinkedNode(Input(inputs, "guider")));
                    var positive = Input(guiderInputs, "positive") is { ValueKind: not JsonValueKind.Undefined } p
                        ? p
                        : Input(guiderInputs, "conditioning");
                    var (samplerName, scheduler, steps, denoise) = ReadSamplerAndSigmas(inputs);
                    return new SamplerSettings(
                        Scalar(Input(Inputs(LinkedNode(Input(inputs, "noise"))), "noise_seed")),
                        steps, Scalar(Input(guiderInputs, "cfg")), samplerName, scheduler, denoise,
                        positive, Input(guiderInputs, "negative"), Input(guiderInputs, "model"), latent);
                }

                case "SamplerCustom":
                {
                    var (samplerName, scheduler, steps, denoise) = ReadSamplerAndSigmas(inputs);
                    return new SamplerSettings(
                        Scalar(Input(inputs, "noise_seed")), steps, Scalar(Input(inputs, "cfg")), samplerName, scheduler, denoise,
                        Input(inputs, "positive"), Input(inputs, "negative"), Input(inputs, "model"), latent);
                }

                default:
                    // KSampler / KSamplerAdvanced / KSampler (Efficient) など
                    return new SamplerSettings(
                        Scalar(Input(inputs, "seed")) ?? Scalar(Input(inputs, "noise_seed")),
                        Scalar(Input(inputs, "steps")),
                        Scalar(Input(inputs, "cfg")),
                        Scalar(Input(inputs, "sampler_name")),
                        Scalar(Input(inputs, "scheduler")),
                        Scalar(Input(inputs, "denoise")),
                        Input(inputs, "positive"), Input(inputs, "negative"), Input(inputs, "model"), latent);
            }
        }

        private GenerationInfo Build(SamplerSettings main, (SamplerSettings Settings, List<JsonElement> Path)? hires, string raw)
        {
            var guidance = new List<string>();
            var prompt = CollectTexts(main.Positive, guidance);
            var negativePrompt = CollectTexts(main.Negative, null);
            var (modelName, loras) = FollowModel(main.Model);

            var parameters = new List<KeyValuePair<string, string>>();
            AddParameter(parameters, "Steps", main.Steps);
            AddParameter(parameters, "Sampler", main.Sampler);
            AddParameter(parameters, "Schedule type", main.Scheduler);
            AddParameter(parameters, "CFG scale", main.Cfg);
            AddParameter(parameters, "Guidance", guidance.FirstOrDefault());
            AddParameter(parameters, "Seed", main.Seed);
            AddParameter(parameters, "Size", FollowSize(main.LatentImage));
            AddParameter(parameters, "Model", modelName);
            AddParameter(parameters, "LoRA", loras.Count > 0 ? string.Join(", ", loras) : null);
            AddParameter(parameters, "Denoising strength", PartialDenoise(main.Denoise));

            if (hires is { } h)
            {
                AddHires(parameters, main, h.Settings, h.Path);
            }

            return new GenerationInfo(_source, NullIfBlank(prompt), NullIfBlank(negativePrompt), parameters, raw);
        }

        /// <summary>
        /// 高解像度化（2回目のパス）の設定を足す。拡大の倍率・大きさ・方法は、間にある拡大のノードから読む
        /// （LatentUpscaleBy / LatentUpscale / ImageScaleBy / ImageScale / ImageUpscaleWithModel）。
        /// サンプラーと CFG スケールは、1回目と違うときだけ出す
        /// </summary>
        private void AddHires(List<KeyValuePair<string, string>> parameters, SamplerSettings main, SamplerSettings hires, List<JsonElement> path)
        {
            string? scale = null, resize = null, upscaler = null;
            bool usesModel = false;
            foreach (var node in path)
            {
                var inputs = Inputs(node);
                scale ??= Scalar(Input(inputs, "scale_by"));
                if (resize is null && Scalar(Input(inputs, "width")) is { } width && Scalar(Input(inputs, "height")) is { } height)
                {
                    resize = $"{width}x{height}";
                }

                // 拡大モデル（ImageUpscaleWithModel → UpscaleModelLoader）があれば、その名前を優先する
                if (Scalar(Input(Inputs(LinkedNode(Input(inputs, "upscale_model"))), "model_name")) is { } model)
                {
                    upscaler = model;
                    usesModel = true;
                }
                else
                {
                    upscaler ??= Scalar(Input(inputs, "upscale_method"));
                }
            }

            // 拡大モデルを使っているときは、モデルの倍率（4x など）に ImageScaleBy の倍率（0.5 など）を掛けたものが本当の倍率で、
            // モデルの倍率は JSON から分からない。間違った倍率を出さないよう、倍率は出さない
            if (usesModel)
            {
                scale = null;
            }

            AddParameter(parameters, "Hires upscale", scale);
            AddParameter(parameters, "Hires resize", resize);
            AddParameter(parameters, "Hires upscaler", upscaler);
            AddParameter(parameters, "Hires steps", hires.Steps);
            AddParameter(parameters, "Hires denoising strength", PartialDenoise(hires.Denoise));
            if (!string.Equals(hires.Sampler, main.Sampler, StringComparison.Ordinal))
            {
                AddParameter(parameters, "Hires sampler", hires.Sampler);
            }
            if (!string.Equals(hires.Cfg, main.Cfg, StringComparison.Ordinal))
            {
                AddParameter(parameters, "Hires CFG scale", hires.Cfg);
            }
        }

        /// <summary>ノイズ除去の強さ。1 は「全部やり直す」（ふつうの txt2img）なので null（出さない）</summary>
        private static string? PartialDenoise(string? denoise) =>
            denoise is not null && !(double.TryParse(denoise, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d >= 1.0)
                ? denoise
                : null;

        /// <summary>
        /// 画像・潜在画像のつながり（<see cref="ImageChainInputs"/>）をさかのぼって、target のノードまでたどれたら、
        /// 間にあるノードを近い順に返す（すぐ target なら空）。たどれなければ null
        /// </summary>
        private List<JsonElement>? PathTo(JsonElement link, string target) =>
            PathTo(link, target, new HashSet<string>(StringComparer.Ordinal), 0);

        private List<JsonElement>? PathTo(JsonElement link, string target, HashSet<string> visited, int depth)
        {
            if (depth > MaxLinkDepth || !TryGetLink(link, out var id, out _))
            {
                return null;
            }
            if (id == target)
            {
                return [];
            }
            if (!visited.Add(id) || !_nodes.TryGetValue(id, out var node))
            {
                return null;
            }

            var inputs = Inputs(node);
            foreach (var key in ImageChainInputs)
            {
                if (PathTo(Input(inputs, key), target, visited, depth + 1) is { } rest)
                {
                    rest.Insert(0, node);
                    return rest;
                }
            }
            return null;
        }

        /// <summary>SamplerCustom 系の sampler（KSamplerSelect）と sigmas（BasicScheduler など）を読む</summary>
        private (string? Sampler, string? Scheduler, string? Steps, string? Denoise) ReadSamplerAndSigmas(JsonElement inputs)
        {
            var samplerInputs = Inputs(LinkedNode(Input(inputs, "sampler")));
            var sigmasInputs = Inputs(LinkedNode(Input(inputs, "sigmas")));
            return (
                Scalar(Input(samplerInputs, "sampler_name")),
                Scalar(Input(sigmasInputs, "scheduler")),
                Scalar(Input(sigmasInputs, "steps")),
                Scalar(Input(sigmasInputs, "denoise")));
        }

        /// <summary>
        /// 条件付け（positive / negative）のつながりをたどって、プロンプトの文字列を集める。
        /// ConditioningCombine などで複数あれば、改行でつなぐ。FluxGuidance があれば、その値も拾う。
        /// </summary>
        private string CollectTexts(JsonElement link, List<string>? guidance)
        {
            var texts = new List<string>();
            CollectTexts(link, texts, guidance, new HashSet<string>(StringComparer.Ordinal), 0);
            return string.Join("\n", texts.Distinct(StringComparer.Ordinal));
        }

        private void CollectTexts(JsonElement link, List<string> texts, List<string>? guidance, HashSet<string> visited, int depth)
        {
            if (depth > MaxLinkDepth || !TryGetLink(link, out var id, out int output) || !visited.Add($"{id}:{output}")
                || !_nodes.TryGetValue(id, out var node))
            {
                return;
            }

            var inputs = Inputs(node);

            // FluxGuidance / CLIPTextEncodeFlux など：guidance を持っていれば拾う
            if (guidance is not null && Scalar(Input(inputs, "guidance")) is { } g)
            {
                guidance.Add(g);
            }

            // ImpactWildcardEncode：ワイルドカードを展開した後の文字列（populated_text）が、実際に使ったプロンプト
            if (Text(Input(inputs, "populated_text"), depth) is { } populated && !string.IsNullOrWhiteSpace(populated))
            {
                texts.Add(populated.Trim());
                return;
            }

            // CLIPTextEncode など：text がプロンプト（別のノードの文字列につながっていることもある）
            bool found = false;
            foreach (var key in PromptInputs)
            {
                if (Text(Input(inputs, key), depth) is { } text && !string.IsNullOrWhiteSpace(text))
                {
                    texts.Add(text.Trim());
                    found = true;
                }
            }
            if (found)
            {
                return;
            }

            // positive と negative を両方持つノード。どちらの出力につながっているかで選ぶ
            // （ControlNetApplyAdvanced などは出力 0 が positive、1 が negative。
            //   Efficient Loader は positive / negative に文字列を直接書き、出力 1 が positive、2 が negative）
            if (Input(inputs, "positive").ValueKind != JsonValueKind.Undefined
                && Input(inputs, "negative").ValueKind != JsonValueKind.Undefined)
            {
                bool isEfficientLoader = ClassType(node) is { } type
                    && (type.Contains("Efficient Loader", StringComparison.Ordinal) || type.Contains("Eff. Loader", StringComparison.Ordinal));
                int positiveOutput = isEfficientLoader ? 1 : 0;
                var chosen = Input(inputs, output == positiveOutput ? "positive" : "negative");

                if (chosen.ValueKind == JsonValueKind.String)
                {
                    if (chosen.GetString() is { } direct && !string.IsNullOrWhiteSpace(direct))
                    {
                        texts.Add(direct.Trim());
                    }
                }
                else
                {
                    CollectTexts(chosen, texts, guidance, visited, depth + 1);
                }
                return;
            }

            // ConditioningCombine / ConditioningSetArea / FluxGuidance など：つながっている条件付けを順にたどる
            foreach (var input in Properties(inputs))
            {
                if (input.Name.StartsWith("conditioning", StringComparison.Ordinal))
                {
                    CollectTexts(input.Value, texts, guidance, visited, depth + 1);
                }
            }
        }

        /// <summary>model のつながりをたどって、チェックポイントの名前と、途中の LoRA（「名前 (強さ)」）を集める</summary>
        private (string? Model, List<string> Loras) FollowModel(JsonElement link)
        {
            var loras = new List<string>();
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (int depth = 0; depth <= MaxLinkDepth; depth++)
            {
                if (!TryGetLink(link, out var id, out _) || !visited.Add(id) || !_nodes.TryGetValue(id, out var node))
                {
                    break;
                }

                var inputs = Inputs(node);
                loras.InsertRange(0, LorasOf(inputs)); // 根元のものから順に並べる

                foreach (var key in new[] { "ckpt_name", "unet_name", "model_name" })
                {
                    if (Scalar(Input(inputs, key)) is { } name)
                    {
                        return (name, loras);
                    }
                }

                link = Input(inputs, "model");
            }
            return (null, loras);
        }

        /// <summary>
        /// 1つのノードに書いてある LoRA を「名前 (強さ)」で返す（書いてある順）。「None」や、オフにしてあるものは除く。
        /// <list type="bullet">
        /// <item>LoraLoader / LoraLoaderModelOnly：lora_name と strength_model</item>
        /// <item>Efficient Loader：lora_name と lora_model_strength</item>
        /// <item>Power Lora Loader (rgthree)：lora_1, lora_2, … に { "on": true, "lora": 名前, "strength": 強さ }</item>
        /// <item>Lora Loader Stack (rgthree)：lora_01, lora_02, … と strength_01, strength_02, …</item>
        /// </list>
        /// </summary>
        private List<string> LorasOf(JsonElement inputs)
        {
            var loras = new List<string>();

            void Add(string? name, string? strength)
            {
                if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
                {
                    loras.Add(strength is null ? name : $"{name} ({strength})");
                }
            }

            if (Scalar(Input(inputs, "lora_name")) is { } single)
            {
                Add(single, Scalar(Input(inputs, "strength_model")) ?? Scalar(Input(inputs, "lora_model_strength")));
            }

            foreach (var input in Properties(inputs))
            {
                if (!input.Name.StartsWith("lora_", StringComparison.Ordinal) || input.Name == "lora_name")
                {
                    continue;
                }

                if (input.Value.ValueKind == JsonValueKind.Object)
                {
                    // Power Lora Loader：オフ（"on": false）のものは使っていない
                    var entry = input.Value;
                    if (Input(entry, "on").ValueKind != JsonValueKind.False)
                    {
                        Add(Scalar(Input(entry, "lora")), Scalar(Input(entry, "strength")));
                    }
                }
                else if (input.Value.ValueKind == JsonValueKind.String)
                {
                    // Lora Loader Stack：lora_01 の強さは strength_01
                    Add(input.Value.GetString(), Scalar(Input(inputs, "strength_" + input.Name["lora_".Length..])));
                }
            }
            return loras;
        }

        /// <summary>latent_image のつながりをたどって、大きさ（「512x768」）を返す。img2img などで分からなければ null</summary>
        private string? FollowSize(JsonElement link)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (int depth = 0; depth <= MaxLinkDepth; depth++)
            {
                if (!TryGetLink(link, out var id, out _) || !visited.Add(id) || !_nodes.TryGetValue(id, out var node))
                {
                    return null;
                }

                var inputs = Inputs(node);
                if (Scalar(Input(inputs, "width")) is { } width && Scalar(Input(inputs, "height")) is { } height)
                {
                    return $"{width}x{height}";
                }

                // Efficient Loader は、空の潜在画像の大きさを自分で持っている
                if (Scalar(Input(inputs, "empty_latent_width")) is { } emptyWidth
                    && Scalar(Input(inputs, "empty_latent_height")) is { } emptyHeight)
                {
                    return $"{emptyWidth}x{emptyHeight}";
                }

                link = Input(inputs, "samples") is { ValueKind: not JsonValueKind.Undefined } samples ? samples : Input(inputs, "latent");
            }
            return null;
        }

        /// <summary>latent_image が EmptyLatentImage など（Empty で始まり Latent を含むノード）から来ているか</summary>
        private bool StartsFromEmptyLatent(JsonElement sampler) =>
            TryGetLink(Input(Inputs(sampler), "latent_image"), out var id, out _)
            && _nodes.TryGetValue(id, out var node)
            && ClassType(node) is { } type
            && type.StartsWith("Empty", StringComparison.Ordinal)
            && type.Contains("Latent", StringComparison.Ordinal);

        /// <summary>
        /// 値を文字列で返す（数値・文字列・真偽値）。別のノードにつながっていたら、そのノードの値をたどる
        /// （シードを Primitive や「Seed」系のカスタムノードから渡している場合など）
        /// </summary>
        private string? Scalar(JsonElement value, int depth = 0)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    return value.GetRawText();
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return value.GetRawText();
                case JsonValueKind.Array when depth < MaxLinkDepth && TryGetLink(value, out var id, out int output)
                                              && _nodes.TryGetValue(id, out var node):
                {
                    var inputs = Inputs(node);

                    // SDXL Prompt Styler など：出力 0 が positive、1 が negative の文字列
                    if (Input(inputs, "text_positive").ValueKind != JsonValueKind.Undefined
                        && Input(inputs, "text_negative").ValueKind != JsonValueKind.Undefined)
                    {
                        return Scalar(Input(inputs, output == 0 ? "text_positive" : "text_negative"), depth + 1);
                    }

                    foreach (var key in ValueInputs)
                    {
                        if (Scalar(Input(inputs, key), depth + 1) is { } found)
                        {
                            return found;
                        }
                    }
                    return null;
                }
                default:
                    return null;
            }
        }

        /// <summary>プロンプトの文字列。別のノード（文字列を出すノード）につながっていたら、そちらの文字列をたどる</summary>
        private string? Text(JsonElement value, int depth) =>
            value.ValueKind is JsonValueKind.String or JsonValueKind.Array ? Scalar(value, depth) : null;

        private JsonElement LinkedNode(JsonElement link) =>
            TryGetLink(link, out var id, out _) && _nodes.TryGetValue(id, out var node) ? node : default;

        /// <summary>["ノード番号", 出力の番号] の形か</summary>
        private static bool TryGetLink(JsonElement value, out string id, out int output)
        {
            id = string.Empty;
            output = 0;
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
            {
                return false;
            }

            var first = value[0];
            var second = value[1];
            id = first.ValueKind switch
            {
                JsonValueKind.String => first.GetString() ?? string.Empty,
                JsonValueKind.Number => first.GetRawText(),
                _ => string.Empty,
            };
            if (second.ValueKind == JsonValueKind.Number && second.TryGetInt32(out int index))
            {
                output = index;
            }
            return id.Length > 0;
        }

        private static string? ClassType(JsonElement node) =>
            node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("class_type", out var type)
            && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;

        private static JsonElement Inputs(JsonElement node) =>
            node.ValueKind == JsonValueKind.Object && node.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Object
                ? inputs
                : default;

        /// <summary>オブジェクトの中身を順に返す。オブジェクトでなければ何も返さない</summary>
        private static IEnumerable<JsonProperty> Properties(JsonElement element) =>
            element.ValueKind == JsonValueKind.Object ? element.EnumerateObject() : [];

        private static JsonElement Input(JsonElement inputs, string name) =>
            inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty(name, out var value) ? value : default;

        private static void AddParameter(List<KeyValuePair<string, string>> parameters, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parameters.Add(new(key, value.Trim()));
            }
        }
    }
}
