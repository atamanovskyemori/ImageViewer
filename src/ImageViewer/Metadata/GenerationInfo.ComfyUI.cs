using System.Globalization;
using System.Text.Json;

namespace ImageViewer.Metadata;

/// <summary>
/// ComfyUI が PNG のテキストチャンク "prompt" に書き込むノード構成（API 形式の JSON）から、生成情報を取り出す。
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
    public static GenerationInfo? ParseComfyUI(string? json)
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
            return new ComfyGraph(document.RootElement).Read(json.Trim());
        }
        catch (JsonException)
        {
            return null; // 壊れた JSON（Python が書く NaN なども含む）。PNG テキストとしてそのまま出す
        }
    }

    /// <summary>ノード構成を読む。JsonDocument が生きている間だけ使う</summary>
    private sealed class ComfyGraph
    {
        private static readonly string[] SamplerTypes =
            ["KSampler", "KSamplerAdvanced", "SamplerCustom", "SamplerCustomAdvanced"];

        private readonly Dictionary<string, JsonElement> _nodes = new(StringComparer.Ordinal);

        public ComfyGraph(JsonElement root)
        {
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

            // 高解像度化などで複数あるときは、空の潜在画像から始めているもの（最初のパス）を主にする
            var main = samplers.FirstOrDefault(s => StartsFromEmptyLatent(s.Value)).Value;
            if (main.ValueKind == JsonValueKind.Undefined)
            {
                main = samplers[0].Value;
            }

            return ReadSampler(main, raw);
        }

        private GenerationInfo ReadSampler(JsonElement sampler, string raw)
        {
            var inputs = Inputs(sampler);
            string type = ClassType(sampler) ?? string.Empty;

            string? seed, steps, cfg, samplerName, scheduler, denoise;
            JsonElement positive, negative, model;

            if (type == "SamplerCustomAdvanced")
            {
                // noise / guider / sampler / sigmas が、それぞれ別のノードになっている（Flux など）
                var noise = LinkedNode(Input(inputs, "noise"));
                var guider = LinkedNode(Input(inputs, "guider"));
                var guiderInputs = Inputs(guider);
                seed = Scalar(Input(Inputs(noise), "noise_seed"));
                cfg = Scalar(Input(guiderInputs, "cfg"));
                positive = Input(guiderInputs, "positive") is { ValueKind: not JsonValueKind.Undefined } p ? p : Input(guiderInputs, "conditioning");
                negative = Input(guiderInputs, "negative");
                model = Input(guiderInputs, "model");
                (samplerName, scheduler, steps, denoise) = ReadSamplerAndSigmas(inputs);
            }
            else if (type == "SamplerCustom")
            {
                seed = Scalar(Input(inputs, "noise_seed"));
                cfg = Scalar(Input(inputs, "cfg"));
                positive = Input(inputs, "positive");
                negative = Input(inputs, "negative");
                model = Input(inputs, "model");
                (samplerName, scheduler, steps, denoise) = ReadSamplerAndSigmas(inputs);
            }
            else
            {
                // KSampler / KSamplerAdvanced
                seed = Scalar(Input(inputs, "seed")) ?? Scalar(Input(inputs, "noise_seed"));
                steps = Scalar(Input(inputs, "steps"));
                cfg = Scalar(Input(inputs, "cfg"));
                samplerName = Scalar(Input(inputs, "sampler_name"));
                scheduler = Scalar(Input(inputs, "scheduler"));
                denoise = Scalar(Input(inputs, "denoise"));
                positive = Input(inputs, "positive");
                negative = Input(inputs, "negative");
                model = Input(inputs, "model");
            }

            var guidance = new List<string>();
            var prompt = CollectTexts(positive, guidance);
            var negativePrompt = CollectTexts(negative, null);
            var (modelName, loras) = FollowModel(model);

            var parameters = new List<KeyValuePair<string, string>>();
            AddParameter(parameters, "Steps", steps);
            AddParameter(parameters, "Sampler", samplerName);
            AddParameter(parameters, "Schedule type", scheduler);
            AddParameter(parameters, "CFG scale", cfg);
            AddParameter(parameters, "Guidance", guidance.FirstOrDefault());
            AddParameter(parameters, "Seed", seed);
            AddParameter(parameters, "Size", FollowSize(Input(inputs, "latent_image")));
            AddParameter(parameters, "Model", modelName);
            AddParameter(parameters, "LoRA", loras.Count > 0 ? string.Join(", ", loras) : null);

            // 1 は「全部やり直す」（ふつうの txt2img）なので出さない
            if (denoise is not null && !(double.TryParse(denoise, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d >= 1.0))
            {
                AddParameter(parameters, "Denoising strength", denoise);
            }

            return new GenerationInfo(GenerationSources.PngComfyUI, NullIfBlank(prompt), NullIfBlank(negativePrompt), parameters, raw);
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

            // CLIPTextEncode など：text がプロンプト（別のノードの文字列につながっていることもある）
            bool found = false;
            foreach (var key in new[] { "text", "text_g", "text_l", "prompt" })
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

            if (guidance is not null && ClassType(node) == "FluxGuidance" && Scalar(Input(inputs, "guidance")) is { } g)
            {
                guidance.Add(g);
            }

            // ControlNetApplyAdvanced などは positive と negative を両方受けて、出力 0 が positive、1 が negative
            if (Input(inputs, "positive").ValueKind != JsonValueKind.Undefined
                && Input(inputs, "negative").ValueKind != JsonValueKind.Undefined)
            {
                CollectTexts(Input(inputs, output == 0 ? "positive" : "negative"), texts, guidance, visited, depth + 1);
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
                if (Scalar(Input(inputs, "lora_name")) is { } lora)
                {
                    var strength = Scalar(Input(inputs, "strength_model"));
                    loras.Insert(0, strength is null ? lora : $"{lora} ({strength})"); // 根元のものから順に並べる
                }

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
                case JsonValueKind.Array when depth < MaxLinkDepth && TryGetLink(value, out var id, out _)
                                              && _nodes.TryGetValue(id, out var node):
                    foreach (var input in Properties(Inputs(node)))
                    {
                        if (input.Name is "value" or "seed" or "noise_seed" or "int" or "float" or "number" or "string" or "text"
                            && Scalar(input.Value, depth + 1) is { } found)
                        {
                            return found;
                        }
                    }
                    return null;
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
