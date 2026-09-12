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
        // CharacterRegistry：命中 / 回退 / 唯一性
        // ------------------------------------------------------------------

        [Test]
        public void GetById_RegisteredId_ReturnsPreset()
        {
            var preset = CharacterRegistry.GetById("slime_2");

            Assert.AreEqual("slime_2", preset.Id);
            Assert.IsFalse(string.IsNullOrEmpty(preset.DisplayName));
            Assert.AreEqual(new Color(0.15f, 0.55f, 0.3f), preset.GlassColor);
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
