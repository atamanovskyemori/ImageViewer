using ImageViewer.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ImageViewer.Tests.Metadata
{
    [TestClass]
    public class ComfyUIGenerationInfoTests
    {
        // The default ComfyUI workflow, as ComfyUI writes it into the PNG "prompt" text chunk
        private const string DefaultWorkflow = """
            {
              "3": {"inputs": {"seed": 156680208700286, "steps": 20, "cfg": 8, "sampler_name": "euler", "scheduler": "normal", "denoise": 1,
                               "model": ["4", 0], "positive": ["6", 0], "negative": ["7", 0], "latent_image": ["5", 0]}, "class_type": "KSampler"},
              "4": {"inputs": {"ckpt_name": "v1-5-pruned-emaonly.safetensors"}, "class_type": "CheckpointLoaderSimple"},
              "5": {"inputs": {"width": 512, "height": 768, "batch_size": 1}, "class_type": "EmptyLatentImage"},
              "6": {"inputs": {"text": "beautiful scenery nature glass bottle landscape, purple galaxy bottle", "clip": ["4", 1]}, "class_type": "CLIPTextEncode"},
              "7": {"inputs": {"text": "text, watermark", "clip": ["4", 1]}, "class_type": "CLIPTextEncode"},
              "8": {"inputs": {"samples": ["3", 0], "vae": ["4", 2]}, "class_type": "VAEDecode"},
              "9": {"inputs": {"filename_prefix": "ComfyUI", "images": ["8", 0]}, "class_type": "SaveImage"}
            }
            """;

        private static GenerationInfo Parse(string json)
        {
            var info = GenerationInfo.ParseComfyUI(json);
            Assert.IsNotNull(info);
            return info;
        }

        [TestMethod]
        public void Default_Workflow_Is_Read()
        {
            var info = Parse(DefaultWorkflow);

            Assert.AreEqual(GenerationSources.PngComfyUI, info.Source);
            Assert.AreEqual("beautiful scenery nature glass bottle landscape, purple galaxy bottle", info.Prompt);
            Assert.AreEqual("text, watermark", info.NegativePrompt);
            Assert.AreEqual("20", info.Get("Steps"));
            Assert.AreEqual("euler", info.Get("Sampler"));
            Assert.AreEqual("normal", info.Get("Schedule type"));
            Assert.AreEqual("8", info.Get("CFG scale"));
            Assert.AreEqual("156680208700286", info.Get("Seed"));
            Assert.AreEqual("512x768", info.Get("Size"));
            Assert.AreEqual("v1-5-pruned-emaonly.safetensors", info.Get("Model"));
            Assert.IsNull(info.Get("Denoising strength"), "1.0 is plain txt2img, so it is not shown");
            Assert.IsNull(info.Get("LoRA"));
        }

        [TestMethod]
        public void Loras_Between_Checkpoint_And_Sampler_Are_Listed_From_The_Checkpoint_Side()
        {
            var info = Parse("""
                {
                  "1": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "base.safetensors"}},
                  "2": {"class_type": "LoraLoader", "inputs": {"lora_name": "style.safetensors", "strength_model": 0.8, "strength_clip": 0.8, "model": ["1", 0], "clip": ["1", 1]}},
                  "3": {"class_type": "LoraLoaderModelOnly", "inputs": {"lora_name": "detail.safetensors", "strength_model": 1, "model": ["2", 0]}},
                  "4": {"class_type": "CLIPTextEncode", "inputs": {"text": "a cat", "clip": ["2", 1]}},
                  "5": {"class_type": "KSampler", "inputs": {"seed": 1, "steps": 10, "cfg": 7, "sampler_name": "euler", "scheduler": "karras", "denoise": 1,
                                                              "model": ["3", 0], "positive": ["4", 0], "negative": ["4", 0], "latent_image": ["6", 0]}},
                  "6": {"class_type": "EmptyLatentImage", "inputs": {"width": 1024, "height": 1024, "batch_size": 1}}
                }
                """);

            Assert.AreEqual("base.safetensors", info.Get("Model"));
            Assert.AreEqual("style.safetensors (0.8), detail.safetensors (1)", info.Get("LoRA"));
        }

        [TestMethod]
        public void Hires_Fix_Uses_The_First_Pass_From_An_Empty_Latent()
        {
            // The upscaling pass has the smaller node id, but the first pass (from the empty latent) is the main one
            var info = Parse("""
                {
                  "1": {"class_type": "KSampler", "inputs": {"seed": 2, "steps": 15, "cfg": 7, "sampler_name": "dpmpp_2m", "scheduler": "karras", "denoise": 0.5,
                                                              "model": ["9", 0], "positive": ["7", 0], "negative": ["8", 0], "latent_image": ["2", 0]}},
                  "2": {"class_type": "LatentUpscaleBy", "inputs": {"upscale_method": "nearest-exact", "scale_by": 1.5, "samples": ["3", 0]}},
                  "3": {"class_type": "KSampler", "inputs": {"seed": 1, "steps": 30, "cfg": 6, "sampler_name": "euler", "scheduler": "normal", "denoise": 1,
                                                              "model": ["9", 0], "positive": ["7", 0], "negative": ["8", 0], "latent_image": ["4", 0]}},
                  "4": {"class_type": "EmptyLatentImage", "inputs": {"width": 832, "height": 1216, "batch_size": 1}},
                  "7": {"class_type": "CLIPTextEncode", "inputs": {"text": "portrait", "clip": ["9", 1]}},
                  "8": {"class_type": "CLIPTextEncode", "inputs": {"text": "bad hands", "clip": ["9", 1]}},
                  "9": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "model.safetensors"}}
                }
                """);

            Assert.AreEqual("30", info.Get("Steps"));
            Assert.AreEqual("1", info.Get("Seed"));
            Assert.AreEqual("832x1216", info.Get("Size"));
            Assert.IsNull(info.Get("Denoising strength"));
        }

        [TestMethod]
        public void Img2Img_Shows_Denoising_Strength()
        {
            var info = Parse(DefaultWorkflow.Replace("\"denoise\": 1", "\"denoise\": 0.6"));

            Assert.AreEqual("0.6", info.Get("Denoising strength"));
        }

        [TestMethod]
        public void Flux_Sampler_Custom_Advanced_Is_Read()
        {
            var info = Parse("""
                {
                  "6": {"class_type": "CLIPTextEncode", "inputs": {"text": "a red fox in the snow", "clip": ["11", 0]}},
                  "8": {"class_type": "VAEDecode", "inputs": {"samples": ["13", 0], "vae": ["10", 0]}},
                  "12": {"class_type": "UNETLoader", "inputs": {"unet_name": "flux1-dev.safetensors", "weight_dtype": "default"}},
                  "13": {"class_type": "SamplerCustomAdvanced", "inputs": {"noise": ["25", 0], "guider": ["22", 0], "sampler": ["16", 0], "sigmas": ["17", 0], "latent_image": ["27", 0]}},
                  "16": {"class_type": "KSamplerSelect", "inputs": {"sampler_name": "euler"}},
                  "17": {"class_type": "BasicScheduler", "inputs": {"scheduler": "simple", "steps": 20, "denoise": 1, "model": ["12", 0]}},
                  "22": {"class_type": "BasicGuider", "inputs": {"model": ["12", 0], "conditioning": ["26", 0]}},
                  "25": {"class_type": "RandomNoise", "inputs": {"noise_seed": 987654321}},
                  "26": {"class_type": "FluxGuidance", "inputs": {"guidance": 3.5, "conditioning": ["6", 0]}},
                  "27": {"class_type": "EmptySD3LatentImage", "inputs": {"width": 1024, "height": 768, "batch_size": 1}}
                }
                """);

            Assert.AreEqual("a red fox in the snow", info.Prompt);
            Assert.IsNull(info.NegativePrompt);
            Assert.AreEqual("20", info.Get("Steps"));
            Assert.AreEqual("euler", info.Get("Sampler"));
            Assert.AreEqual("simple", info.Get("Schedule type"));
            Assert.AreEqual("3.5", info.Get("Guidance"));
            Assert.AreEqual("987654321", info.Get("Seed"));
            Assert.AreEqual("1024x768", info.Get("Size"));
            Assert.AreEqual("flux1-dev.safetensors", info.Get("Model"));
        }

        [TestMethod]
        public void Prompt_And_Seed_From_Linked_Nodes_Are_Followed()
        {
            var info = Parse("""
                {
                  "1": {"class_type": "KSampler", "inputs": {"seed": ["5", 0], "steps": 20, "cfg": 7, "sampler_name": "euler", "scheduler": "normal", "denoise": 1,
                                                              "model": ["2", 0], "positive": ["3", 0], "negative": ["4", 0], "latent_image": ["6", 0]}},
                  "2": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "m.safetensors"}},
                  "3": {"class_type": "CLIPTextEncode", "inputs": {"text": ["7", 0], "clip": ["2", 1]}},
                  "4": {"class_type": "CLIPTextEncode", "inputs": {"text": "", "clip": ["2", 1]}},
                  "5": {"class_type": "Seed (rgthree)", "inputs": {"seed": 42}},
                  "6": {"class_type": "EmptyLatentImage", "inputs": {"width": 512, "height": 512, "batch_size": 1}},
                  "7": {"class_type": "PrimitiveStringMultiline", "inputs": {"value": "linked prompt\nsecond line"}}
                }
                """);

            Assert.AreEqual("linked prompt\nsecond line", info.Prompt);
            Assert.IsNull(info.NegativePrompt, "an empty negative prompt is not shown");
            Assert.AreEqual("42", info.Get("Seed"));
        }

        [TestMethod]
        public void ControlNet_Passes_Positive_And_Negative_Through_Its_Outputs()
        {
            var info = Parse("""
                {
                  "1": {"class_type": "KSampler", "inputs": {"seed": 1, "steps": 20, "cfg": 7, "sampler_name": "euler", "scheduler": "normal", "denoise": 1,
                                                              "model": ["9", 0], "positive": ["5", 0], "negative": ["5", 1], "latent_image": ["8", 0]}},
                  "3": {"class_type": "CLIPTextEncode", "inputs": {"text": "good", "clip": ["9", 1]}},
                  "4": {"class_type": "CLIPTextEncode", "inputs": {"text": "bad", "clip": ["9", 1]}},
                  "5": {"class_type": "ControlNetApplyAdvanced", "inputs": {"strength": 1, "positive": ["3", 0], "negative": ["4", 0], "control_net": ["6", 0], "image": ["7", 0]}},
                  "8": {"class_type": "EmptyLatentImage", "inputs": {"width": 512, "height": 512, "batch_size": 1}},
                  "9": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "m.safetensors"}}
                }
                """);

            Assert.AreEqual("good", info.Prompt);
            Assert.AreEqual("bad", info.NegativePrompt);
        }

        [TestMethod]
        public void Combined_Conditionings_Are_Joined_With_New_Lines()
        {
            var info = Parse("""
                {
                  "1": {"class_type": "KSampler", "inputs": {"seed": 1, "steps": 20, "cfg": 7, "sampler_name": "euler", "scheduler": "normal", "denoise": 1,
                                                              "model": ["9", 0], "positive": ["5", 0], "negative": ["4", 0], "latent_image": ["8", 0]}},
                  "2": {"class_type": "CLIPTextEncode", "inputs": {"text": "a forest", "clip": ["9", 1]}},
                  "3": {"class_type": "CLIPTextEncode", "inputs": {"text": "a castle", "clip": ["9", 1]}},
                  "4": {"class_type": "CLIPTextEncode", "inputs": {"text": "blurry", "clip": ["9", 1]}},
                  "5": {"class_type": "ConditioningCombine", "inputs": {"conditioning_1": ["2", 0], "conditioning_2": ["3", 0]}},
                  "8": {"class_type": "EmptyLatentImage", "inputs": {"width": 512, "height": 512, "batch_size": 1}},
                  "9": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "m.safetensors"}}
                }
                """);

            Assert.AreEqual("a forest\na castle", info.Prompt);
        }

        [TestMethod]
        public void Sdxl_Text_Encoder_Is_Read()
        {
            var info = Parse(DefaultWorkflow.Replace(
                "\"text\": \"beautiful scenery nature glass bottle landscape, purple galaxy bottle\", \"clip\": [\"4\", 1]}, \"class_type\": \"CLIPTextEncode\"",
                "\"text_g\": \"sdxl prompt\", \"text_l\": \"sdxl prompt\", \"width\": 1024, \"height\": 1024, \"clip\": [\"4\", 1]}, \"class_type\": \"CLIPTextEncodeSDXL\""));

            Assert.AreEqual("sdxl prompt", info.Prompt, "the same text in text_g and text_l is shown once");
        }

        [TestMethod]
        [DataRow("")]
        [DataRow("not json")]
        [DataRow("[1, 2, 3]")]
        [DataRow("{\"1\": {\"class_type\": \"CLIPTextEncode\", \"inputs\": {\"text\": \"no sampler\"}}}")]
        [DataRow("{\"1\": {\"class_type\": \"KSampler\", \"inputs\": {\"cfg\": NaN}}}")]
        public void Unreadable_Or_Samplerless_Json_Gives_Null(string json)
        {
            Assert.IsNull(GenerationInfo.ParseComfyUI(json));
        }

        [TestMethod]
        public void Broken_Links_And_Loops_Do_Not_Stop_Reading()
        {
            var info = Parse("""
                {
                  "1": {"class_type": "KSampler", "inputs": {"seed": 5, "steps": "oops", "model": ["99", 0], "positive": ["2", 0], "negative": [1, 2, 3], "latent_image": "x"}},
                  "2": {"class_type": "ConditioningCombine", "inputs": {"conditioning_1": ["3", 0], "conditioning_2": ["2", 0]}},
                  "3": {"class_type": "ConditioningCombine", "inputs": {"conditioning_1": ["2", 0]}},
                  "4": "not a node"
                }
                """);

            Assert.IsNull(info.Prompt);
            Assert.AreEqual("5", info.Get("Seed"));
            Assert.AreEqual("oops", info.Get("Steps"));
            Assert.IsNull(info.Get("Model"));
            Assert.IsNull(info.Get("Size"));
        }

        [TestMethod]
        public void Info_Panel_Shows_ComfyUI_Info_And_Hides_The_Raw_Prompt_Text()
        {
            var generation = Parse(DefaultWorkflow);
            var facts = new FileFacts(@"C:\Out\ComfyUI_00001_.png", 1000, null, "PNG", 512, 768, false);
            var texts = new[] { new PngText("prompt", DefaultWorkflow), new PngText("workflow", "{\"nodes\": []}") };

            var info = ImageInfoBuilder.Build(facts, null, generation, texts);

            var genSection = info.Sections.Single(s => s.Title == "生成情報");
            Assert.AreEqual("text, watermark", genSection.Rows.Single(r => r.Label == "ネガティブプロンプト").Value);
            Assert.AreEqual("euler", genSection.Rows.Single(r => r.Label == "サンプラー").Value);

            var textSection = info.Sections.Single(s => s.Title == "PNG テキスト");
            CollectionAssert.AreEqual(new[] { "workflow" }, textSection.Rows.Select(r => r.Label).ToArray());
        }

        [TestMethod]
        public void Guidance_And_Lora_Have_Japanese_Labels()
        {
            var info = Parse("""
                {
                  "1": {"class_type": "UNETLoader", "inputs": {"unet_name": "flux.safetensors"}},
                  "2": {"class_type": "LoraLoaderModelOnly", "inputs": {"lora_name": "x.safetensors", "strength_model": 0.7, "model": ["1", 0]}},
                  "3": {"class_type": "CLIPTextEncode", "inputs": {"text": "p"}},
                  "4": {"class_type": "FluxGuidance", "inputs": {"guidance": 4, "conditioning": ["3", 0]}},
                  "5": {"class_type": "KSampler", "inputs": {"seed": 1, "steps": 20, "cfg": 1, "sampler_name": "euler", "scheduler": "simple", "denoise": 1,
                                                              "model": ["2", 0], "positive": ["4", 0], "negative": ["3", 0], "latent_image": ["6", 0]}},
                  "6": {"class_type": "EmptySD3LatentImage", "inputs": {"width": 512, "height": 512, "batch_size": 1}}
                }
                """);
            var facts = new FileFacts(@"C:\Out\a.png", 1000, null, "PNG", 512, 512, false);

            var rows = ImageInfoBuilder.Build(facts, null, info).Sections.Single(s => s.Title == "生成情報").Rows;

            Assert.AreEqual("4", rows.Single(r => r.Label == "ガイダンス").Value);
            Assert.AreEqual("x.safetensors (0.7)", rows.Single(r => r.Label == "LoRA").Value);
            Assert.AreEqual("flux.safetensors", rows.Single(r => r.Label == "モデル").Value);
        }
    }
}
