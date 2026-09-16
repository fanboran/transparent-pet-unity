// ============================================================================
// PetManager.cs — 多桌宠管理器（第一块：贴图物种）
// ============================================================================
// 定位：多桌宠管理的第一个增量切片——只管"贴图生命感史莱姆"（TexturedPet）的
// 动态增删。后续轮次在这里合并 PBF/液态玻璃物种的纳管，并统一做位置持久化。
//
// 数据通路：原生设置窗口（NativeSettingsWindow，专用 UI 线程）把按钮点击写进
// ManagerChanges 并发队列 → 本组件在主线程 Update 里逐条取出应用（Unity API
// 非线程安全，跨线程只传 SettingChange 数据，不传调用——与 Changes 队列同一套路）。
//
// 贴图资源：PetSlime.png 已移入 Assets/Resources，运行时 Resources.Load 懒加载并缓存。
// ============================================================================
using System.Collections.Generic;
using TransparentPet.Core;
using UnityEngine;

namespace TransparentPet.Pet
{
    /// <summary>贴图物种的动态增删管理（挂在场景组装根下，由 SceneGenerator 装配）。</summary>
    public class PetManager : MonoBehaviour
    {
        /// <summary>贴图物种同屏上限（与液态玻璃"至多 3 只"对齐），超限忽略</summary>
        const int MaxTextured = 3;

        /// <summary>新增时与最后一只的横向间距（屏像素，约一只身位）</summary>
        const float SpawnGapPx = 220f;

        /// <summary>出生位置随机抖动半径（屏像素）：避免一排整齐"排排站"</summary>
        const float SpawnJitterPx = 24f;

        /// <summary>Resources 里的贴图资源名（Assets/Resources/PetSlime.png）</summary>
        const string PetTextureResourceName = "PetSlime";

        readonly List<TexturedPet> texturedPets = new List<TexturedPet>();
        Texture2D petTexture; // Resources 懒加载缓存（加载失败保持 null，下次添加时重试）

        /// <summary>当前贴图物种数量（装配方/测试读取）</summary>
        public int TexturedCount => texturedPets.Count;

        void Update()
        {
            // 消费设置窗口的多桌宠管理变更（主线程；队列空则零开销）
            while (NativeSettingsWindow.ManagerChanges.TryDequeue(out var change))
            {
                switch (change.Key)
                {
                    case "addtextured":
                        AddTextured();
                        break;
                    case "removetextured":
                        RemoveLastTextured();
                        break;
                }
            }
        }

        /// <summary>添加一只贴图史莱姆：最后一只右侧 220px，无则屏幕中央；超上限忽略。</summary>
        void AddTextured()
        {
            if (texturedPets.Count >= MaxTextured)
                return;

            if (petTexture == null)
            {
                petTexture = Resources.Load<Texture2D>(PetTextureResourceName);
                if (petTexture == null)
                {
                    Debug.LogError("[PetManager] Resources.Load 找不到 " + PetTextureResourceName + " 贴图，无法添加贴图史莱姆");
                    return;
                }
            }

            texturedPets.RemoveAll(p => p == null); // 清掉被外部销毁的空引用（Unity 伪 null）

            // 摆位：最后一只右侧 220px；一只都没有则屏幕中央；叠加随机抖动
            var spawnPx = texturedPets.Count > 0
                ? texturedPets[texturedPets.Count - 1].ScreenPosPx + new Vector2(SpawnGapPx, 0f)
                : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            spawnPx += new Vector2(
                Random.Range(-SpawnJitterPx, SpawnJitterPx),
                Random.Range(-SpawnJitterPx, SpawnJitterPx));

            texturedPets.Add(TexturedPet.Create(transform, petTexture, spawnPx));
        }

        /// <summary>销毁最后一只贴图史莱姆；本物种可为 0 只，空了就忽略。</summary>
        void RemoveLastTextured()
        {
            texturedPets.RemoveAll(p => p == null);
            if (texturedPets.Count == 0)
                return;

            var last = texturedPets[texturedPets.Count - 1];
            texturedPets.RemoveAt(texturedPets.Count - 1);
            Destroy(last.gameObject);
        }
    }
}
