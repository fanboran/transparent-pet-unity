"""性能改造前后的快照对比辅助脚本（临时工具，不入库）。

用法：python tools/perfcmp.py <before_dir> <after_dir> <out_dir> [放大倍数]
产出：每个用例的三联对比图（左=改前 / 中=改后 / 右=差异×10），供人工看图判断。
"""
import os
import sys

import numpy as np
from PIL import Image


def up(x, scale):
    if x.ndim == 3:
        return np.kron(x[:, :, :3], np.ones((scale, scale, 1))).astype(np.uint8)
    return np.kron(x, np.ones((scale, scale))).astype(np.uint8)


def main():
    before, after, out = sys.argv[1], sys.argv[2], sys.argv[3]
    scale = int(sys.argv[4]) if len(sys.argv) > 4 else 2
    os.makedirs(out, exist_ok=True)
    for name in sorted(f for f in os.listdir(before) if f.endswith('.png')):
        a = np.asarray(Image.open(os.path.join(before, name)).convert('RGBA')).astype(np.int16)
        b = np.asarray(Image.open(os.path.join(after, name)).convert('RGBA')).astype(np.int16)
        if a.shape != b.shape:
            print(f"{name}: 尺寸不同，跳过")
            continue
        d = np.abs(a - b).max(axis=2)
        ys, xs = np.nonzero(d)
        if len(ys) == 0:
            print(f"{name}: 逐像素一致")
            continue
        # 只裁剪有差异的区域，外扩 40px，避免整图缩放后看不清
        y0, y1 = max(0, ys.min() - 40), min(a.shape[0], ys.max() + 41)
        x0, x1 = max(0, xs.min() - 40), min(a.shape[1], xs.max() + 41)
        A, B, D = a[y0:y1, x0:x1], b[y0:y1, x0:x1], d[y0:y1, x0:x1]
        Au, Bu = up(A, scale), up(B, scale)
        Du = np.stack([up(np.clip(D * 10, 0, 255), scale)] * 3, axis=2)
        gap = np.zeros((Au.shape[0], 6, 3), np.uint8)
        Image.fromarray(np.concatenate([Au, gap, Bu, gap, Du], axis=1)).save(
            os.path.join(out, name))
        print(f"{name}: 差异 {len(ys)} 像素 max={int(d.max())}，裁剪区 ({x0},{y0})-({x1},{y1})")


if __name__ == '__main__':
    main()
