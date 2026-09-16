using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using TransparentPet.Core;
using TransparentPet.Pet;

namespace TransparentPet.Tests
{
    /// <summary>
    /// PetConfigStore 与 CharacterRegistry 单元测试。
    /// 配置存取全部走临时文件路径注入，不触碰真实 persistentDataPath。
    /// </summary>
    public class PetConfigAndRegistryTests
    {
        // ------------------------------------------------------------------
        // PetConfigStore：Save → Load 往返
        // ------------------------------------------------------------------

        [Test]
        public void SaveLoad_RoundTrip_PreservesAllFields()
        {
            var path = Path.GetTempFileName();
            try
            {
                var config = new PetConfig
                {
                    petScale = 0.5f,
                    characterId = "slime_2",
                    alwaysOnTop = false,
                    autoStart = true,
                    throwParams = new ThrowParams
                    {
                        gravity = 900f,
                        minSpeed = 300f,
                        maxSpeed = 700f,
                        multiplier = 1.5f,
                        enabled = false
                    },
                    petScreenX = 1920f,
                    petScreenY = 108f
                };

                PetConfigStore.Save(config, path);
                var loaded = PetConfigStore.Load(path);

                Assert.AreEqual(0.5f, loaded.petScale);
                Assert.AreEqual("slime_2", loaded.characterId);
                Assert.IsFalse(loaded.alwaysOnTop);
                Assert.IsTrue(loaded.autoStart);
                Assert.AreEqual(900f, loaded.throwParams.gravity);
                Assert.AreEqual(300f, loaded.throwParams.minSpeed);
                Assert.AreEqual(700f, loaded.throwParams.maxSpeed);
                Assert.AreEqual(1.5f, loaded.throwParams.multiplier);
                Assert.IsFalse(loaded.throwParams.enabled);
                Assert.AreEqual(1920f, loaded.petScreenX);
                Assert.AreEqual(108f, loaded.petScreenY);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Test]
        public void Load_MissingFile_ReturnsDefaults()
        {
            var missingPath = Path.Combine(
                Path.GetTempPath(), "pet_config_missing_" + Guid.NewGuid().ToString("N") + ".json");

            var loaded = PetConfigStore.Load(missingPath);

            Assert.AreEqual(1f, loaded.petScale);
            Assert.AreEqual("slime_1", loaded.characterId);
            Assert.IsTrue(loaded.alwaysOnTop);
            Assert.IsFalse(loaded.autoStart);
            Assert.AreEqual(800f, loaded.throwParams.gravity);
            Assert.AreEqual(350f, loaded.throwParams.minSpeed);
            Assert.AreEqual(800f, loaded.throwParams.maxSpeed);
            Assert.AreEqual(2f, loaded.throwParams.multiplier);
            Assert.IsTrue(loaded.throwParams.enabled);
            Assert.AreEqual(-1f, loaded.petScreenX);
            Assert.AreEqual(-1f, loaded.petScreenY);
        }

        [Test]
        public void Load_CorruptedJson_ReturnsDefaultsWithoutThrowing()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "{ 这不是合法的 JSON ]]");

                Assert.DoesNotThrow(() =>
                {
                    var loaded = PetConfigStore.Load(path);
                    Assert.AreEqual(1f, loaded.petScale);
                    Assert.AreEqual("slime_1", loaded.characterId);
                    Assert.AreEqual(800f, loaded.throwParams.gravity);
                });
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        // ------------------------------------------------------------------
        // PetConfigStore：多桌宠位置数组（玻璃 / 果冻软体）往返
        // ------------------------------------------------------------------

        [Test]
        public void SaveLoad_RoundTrip_PreservesPerPetArrays()
        {
            var path = Path.GetTempFileName();
            try
            {
                var config = new PetConfig
                {
                    glassSlimeX = new[] { 100f, 420f, 900f },
                    glassSlimeY = new[] { 200f, 220f, 260f },
                    glassSlimeKind = new[] { 0, 2, 1 },
                    softbodyX = new[] { 300f, 700f },
                    softbodyY = new[] { 350f, 360f },
                    texturedX = new[] { 1100f },
                    texturedY = new[] { 500f },
                    ringsplitX = new[] { 1400f },
                    ringsplitY = new[] { 300f },
                };

                PetConfigStore.Save(config, path);
                var loaded = PetConfigStore.Load(path);

                Assert.AreEqual(3, loaded.glassSlimeX.Length);
                Assert.AreEqual(900f, loaded.glassSlimeX[2]);
                Assert.AreEqual(2, loaded.glassSlimeKind[1]);
                Assert.AreEqual(2, loaded.softbodyX.Length);
                Assert.AreEqual(700f, loaded.softbodyX[1]);
                Assert.AreEqual(360f, loaded.softbodyY[1]);
                Assert.AreEqual(1, loaded.texturedX.Length);
                Assert.AreEqual(1100f, loaded.texturedX[0]);
                Assert.AreEqual(500f, loaded.texturedY[0]);
                Assert.AreEqual(1, loaded.ringsplitX.Length);
                Assert.AreEqual(1400f, loaded.ringsplitX[0]);
                Assert.AreEqual(300f, loaded.ringsplitY[0]);
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Test]
        public void Load_MissingFile_PerPetArraysAreEmpty()
        {
            var missingPath = Path.Combine(
                Path.GetTempPath(), "pet_config_missing_" + Guid.NewGuid().ToString("N") + ".json");

            var loaded = PetConfigStore.Load(missingPath);

            // 空数组 = 从未保存过 → 管理器按"没有该物种"处理，绝不还原出垃圾位置
            Assert.AreEqual(0, loaded.softbodyX.Length);
            Assert.AreEqual(0, loaded.softbodyY.Length);
            Assert.AreEqual(0, loaded.texturedX.Length);
            Assert.AreEqual(0, loaded.texturedY.Length);
            Assert.AreEqual(0, loaded.ringsplitX.Length);
            Assert.AreEqual(0, loaded.ringsplitY.Length);
            Assert.AreEqual(0, loaded.glassSlimeX.Length);
        }

        // ------------------------------------------------------------------
        // PetSpeciesCatalog：物种注册表（多桌宠管理的物种清单）
        // ------------------------------------------------------------------

        [Test]
        public void SpeciesCatalog_GlassIsIndexZero_ForwardCompat()
        {
            // 液态玻璃固定在 0：设置变更 "add:0" 与旧配置都依赖这一约定
            Assert.AreEqual("glass", PetSpeciesCatalog.All[0].Id);
            Assert.AreEqual(0, PetSpeciesCatalog.IndexOf("glass"));
        }

        [Test]
        public void SpeciesCatalog_ContainsFinalizedFourSpecies()
        {
            // 用户拍板的"四种一起存在"：定稿三物种（贴图/果冻/分裂——当年展厅同屏的
            // 三路观感）+ 后来加入的液态玻璃 = 四种并列；顺序即物种索引，勿重排
            Assert.AreEqual(4, PetSpeciesCatalog.All.Count);
            Assert.AreEqual("glass", PetSpeciesCatalog.All[0].Id);
            Assert.AreEqual("textured", PetSpeciesCatalog.All[1].Id);
            Assert.AreEqual("softbody", PetSpeciesCatalog.All[2].Id);
            Assert.AreEqual("ringsplit", PetSpeciesCatalog.All[3].Id);
        }

        [Test]
        public void SpeciesCatalog_IdsUnique_AndCapsPositive()
        {
            var seen = new HashSet<string>();
            foreach (var species in PetSpeciesCatalog.All)
            {
                Assert.IsTrue(seen.Add(species.Id), $"物种 id 重复：{species.Id}");
                Assert.Greater(species.MaxCount, 0);
                Assert.IsFalse(string.IsNullOrEmpty(species.DisplayName));
            }
            Assert.GreaterOrEqual(PetSpeciesCatalog.All.Count, 2); // 至少：液态玻璃 + 果冻软体
        }

        [Test]
        public void SpeciesCatalog_UnknownId_ReturnsMinusOne()
        {
            Assert.AreEqual(-1, PetSpeciesCatalog.IndexOf("不存在的物种"));
        }

        // ------------------------------------------------------------------
        // CharacterRegistry：命中 / 回退 / 唯一性
        // ------------------------------------------------------------------

        [Test]
        public void GetById_RegisteredId_ReturnsPreset()
        {
            var preset = CharacterRegistry.GetById("slime_2");

            Assert.AreEqual("slime_2", preset.Id);
            Assert.IsFalse(string.IsNullOrEmpty(preset.DisplayName));
            Assert.AreEqual(new Color(0.22f, 0.75f, 0.4f), preset.GlassColor); // 亮度锚定原版观感（2026-09 提亮）
        }

        [Test]
        public void GetById_UnknownId_FallsBackToSlime1()
        {
            var preset = CharacterRegistry.GetById("不存在的角色_id");

            Assert.AreEqual("slime_1", preset.Id);
        }

        [Test]
        public void IsValid_OnlyAcceptsRegisteredIds()
        {
            Assert.IsTrue(CharacterRegistry.IsValid("slime_1"));
            Assert.IsTrue(CharacterRegistry.IsValid("slime_3"));
            Assert.IsFalse(CharacterRegistry.IsValid("slime_999"));
            Assert.IsFalse(CharacterRegistry.IsValid(null));
        }

        [Test]
        public void All_HasAtLeastThreePresets_WithUniqueIds()
        {
            Assert.GreaterOrEqual(CharacterRegistry.All.Count, 3);

            var seen = new HashSet<string>();
            foreach (var preset in CharacterRegistry.All)
                Assert.IsTrue(seen.Add(preset.Id), $"角色 id 重复：{preset.Id}");
        }
    }
}
