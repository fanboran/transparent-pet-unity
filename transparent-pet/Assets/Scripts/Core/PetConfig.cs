// 宠物配置模型 + JSON 持久化（对应 Godot 版 ConfigFile 存取，改用 JsonUtility）
using System;
using System.IO;
using UnityEngine;

namespace TransparentPet.Core
{
    /// <summary>抛射物理可调参数（载荷见 EventTopics.ThrowParamsChanged）。</summary>
    [Serializable]
    public class ThrowParams
    {
        /// <summary>重力加速度（px/s²）</summary>
        public float gravity = 800f;

        /// <summary>松手触发抛射的最小初速（px/s），低于此值视为放下</summary>
        public float minSpeed = 350f;

        /// <summary>抛射初速上限（px/s）</summary>
        public float maxSpeed = 800f;

        /// <summary>拖拽速度 → 抛射初速的放大倍率</summary>
        public float multiplier = 2f;

        /// <summary>抛射物理总开关</summary>
        public bool enabled = true;
    }

    /// <summary>宠物全部持久化配置，由 PetConfigStore 负责 JSON 读写。</summary>
    [Serializable]
    public class PetConfig
    {
        /// <summary>相对基准缩放，合法范围 0.25~2.0</summary>
        public float petScale = 1f;

        /// <summary>当前角色 id（见 CharacterRegistry）</summary>
        public string characterId = "slime_1";

        /// <summary>窗口是否置顶</summary>
        public bool alwaysOnTop = true;

        /// <summary>开机自启动（托盘菜单用）</summary>
        public bool autoStart = false;

        /// <summary>抛射物理参数</summary>
        public ThrowParams throwParams = new ThrowParams();

        /// <summary>宠物屏幕像素 X 坐标（左上原点），-1 表示从未保存过位置</summary>
        public float petScreenX = -1f;

        /// <summary>宠物屏幕像素 Y 坐标（左上原点），-1 表示从未保存过位置</summary>
        public float petScreenY = -1f;

        /// <summary>是否已展示过首次启动引导（HUD 提示只出现一次）</summary>
        public bool introShown = false;

        /// <summary>抓屏隐形（液态玻璃折射真实桌面的前置）：本窗口设
        /// WDA_EXCLUDEFROMCAPTURE，代价是录屏/直播/截图里桌宠消失</summary>
        public bool captureInvisible = false;

        /// <summary>液态玻璃各只史莱姆的屏幕位置（左上原点，成对使用）。
        /// 空数组 = 从未保存过（回退单只居中）；上限与 LiquidGlassController.MaxSlimes /
        /// LiquidGlass.shader 的 MAX_ITEMS 对齐（16）。</summary>
        public float[] glassSlimeX = System.Array.Empty<float>();
        public float[] glassSlimeY = System.Array.Empty<float>();
    }

    /// <summary>
    /// PetConfig 的 JSON 存取门面。filePath 参数可注入：传临时文件路径即可
    /// 在 NUnit 测试中脱离 persistentDataPath 做往返验证。
    /// </summary>
    public static class PetConfigStore
    {
        /// <summary>默认配置文件路径：persistentDataPath/config.json</summary>
        public static string DefaultFilePath =>
            Path.Combine(Application.persistentDataPath, "config.json");

        /// <summary>
        /// 读取配置。文件不存在、内容为空、JSON 损坏等一切异常情况
        /// 均返回带默认字段值的新 PetConfig，绝不抛异常。
        /// </summary>
        public static PetConfig Load(string filePath = null)
        {
            var path = filePath ?? DefaultFilePath;
            try
            {
                if (!File.Exists(path))
                    return new PetConfig();

                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return new PetConfig();

                var config = JsonUtility.FromJson<PetConfig>(json);
                return config ?? new PetConfig(); // 合法 JSON 的 "null"/空对象等边界也兜底
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PetConfigStore] 读取配置失败，回退默认配置：{path}\n{e.Message}");
                return new PetConfig();
            }
        }

        /// <summary>保存配置为缩进 JSON；目标目录不存在时自动创建。写入异常向上抛给调用方。
        /// 走原子写入（临时文件 + 替换）：崩溃安全，正式文件绝无半截损坏。</summary>
        public static void Save(PetConfig config, string filePath = null)
        {
            var path = filePath ?? DefaultFilePath;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            AtomicWrite.WriteAllText(path, JsonUtility.ToJson(config, true));
        }
    }
}
