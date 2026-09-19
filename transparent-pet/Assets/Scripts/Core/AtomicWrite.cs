// ============================================================================
// AtomicWrite.cs — 原子文件写入工具（纯 BCL，崩溃安全的落盘原语）
// ============================================================================
// 为什么不直接 File.WriteAllText：写一半崩溃/断电会让正式文件留下半截损坏内容，
// 下次启动的容错读只好回退默认值——用户全部设置静默丢失（config.json /
// summoned_pets.json 都吃过这个亏）。先写临时文件再替换，最坏情况只留下 .tmp
// 残骸，正式文件要么是旧版要么是新版，绝无半截。
// ============================================================================
using System.IO;

namespace TransparentPet.Core
{
    /// <summary>原子文件写入：先写临时文件再替换，杜绝"写一半崩溃损坏正式文件"。</summary>
    public static class AtomicWrite
    {
        /// <summary>原子写文本。崩溃最坏留下 .tmp 残骸，正式文件要么旧版要么新版，绝无半截。</summary>
        public static void WriteAllText(string path, string content)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            // 已有正式文件用 File.Replace（原子交换 + 顺带清掉旧文件）；首写用 Move。
            // 注意 .NET Standard 2.1 没有 File.Move(string, string, bool) 重载，
            // 故按存在性分叉，不能一把 Move(overwrite: true)。
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }
    }
}
