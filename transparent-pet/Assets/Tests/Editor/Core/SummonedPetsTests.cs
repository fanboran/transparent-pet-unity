using NUnit.Framework;
using TransparentPet.Pet;
using UnityEngine;

namespace TransparentPet.Tests
{
    /// <summary>物种召唤持久化（summoned_pets.json）的序列化往返规格。</summary>
    public class SummonedPetsTests
    {
        [Test]
        public void SerializeDeserialize_RoundTrip_PreservesRecords()
        {
            var list = new SpeciesPets.PetRecList
            {
                pets =
                {
                    new SpeciesPets.PetRec { kind = "textured", x = 100.5f, y = 200.25f },
                    new SpeciesPets.PetRec { kind = "softbody", x = 300f, y = 400f },
                    new SpeciesPets.PetRec { kind = "mesh", x = -10f, y = 0f },
                }
            };

            var parsed = SpeciesPets.Deserialize(SpeciesPets.Serialize(list));

            Assert.AreEqual(3, parsed.Count, "往返后记录数应一致");
            Assert.AreEqual("textured", parsed[0].kind);
            Assert.AreEqual(100.5f, parsed[0].x, 1e-4f);
            Assert.AreEqual(200.25f, parsed[0].y, 1e-4f);
            Assert.AreEqual("mesh", parsed[2].kind);
            Assert.AreEqual(-10f, parsed[2].x, 1e-4f);
        }

        [Test]
        public void Deserialize_EmptyOrCorruptJson_ReturnsEmptyList()
        {
            Assert.AreEqual(0, SpeciesPets.Deserialize(null).Count, "null 应按无宠物处理");
            Assert.AreEqual(0, SpeciesPets.Deserialize("").Count, "空串应按无宠物处理");
            Assert.AreEqual(0, SpeciesPets.Deserialize("{{{not json").Count, "损坏文件应按无宠物处理（不抛异常）");
        }
    }
}
