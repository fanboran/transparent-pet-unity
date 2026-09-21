// ============================================================================
// TrayMenuState.cs — 托盘菜单的动态状态计算（纯逻辑，可 NUnit）
// ============================================================================
// 菜单**结构**是静态的，**状态**是动态的（勾选 / 单选组点哪一档 / 这项能不能点）。
// 刷新回调（PetWindowSetup.RefreshTrayMenu）把配置文件与注册表的当前值搬进
// TrayMenuItem 的字段，其中唯一有判断成分的是"当前缩放该点亮哪个档位"：
// 滑条给出的可能是 1.0499999 这种值，不能用 == 比，得取最近的档位。
// 抽到这里让它可测（Platform 的托盘逻辑本来就有 FlattenLeaves / ReaddRetryState
// 两个纯逻辑单元，这里是第三个）。
// ============================================================================
using System.Collections.Generic;
using UnityEngine;

namespace TransparentPet.Platform
{
    public static class TrayMenuState
    {
        /// <summary>
        /// 与 value 最接近的档位下标（并列取靠前者）；空表返回 -1。
        /// 用于单选组：把整组标 Radio，只把返回的这一项 Checked。
        /// </summary>
        public static int NearestPresetIndex(float value, IReadOnlyList<float> presets)
        {
            if (presets == null || presets.Count == 0)
                return -1;

            var best = 0;
            var bestDelta = Mathf.Abs(presets[0] - value);
            for (var i = 1; i < presets.Count; i++)
            {
                var delta = Mathf.Abs(presets[i] - value);
                if (delta >= bestDelta)
                    continue; // 并列取靠前者：用 >= 跳过，保持 best 不动
                best = i;
                bestDelta = delta;
            }
            return best;
        }
    }
}
