// ============================================================================
// RoleBootstrap.cs — 引导场景：按角色切到玻璃/物种场景（双窗口双进程的入口分岔）
// ============================================================================
using UnityEngine;
using UnityEngine.SceneManagement;
using TransparentPet.Pet;

namespace TransparentPet.Core
{
    public class RoleBootstrap : MonoBehaviour
    {
        void Awake()
        {
            SceneManager.LoadScene(RoleEnvironment.IsSpecies ? "Species" : "Glass");
        }
    }
}
