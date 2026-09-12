# -*- coding: utf-8 -*-
"""把 ProductShots 渲染的帧序列合成为 GIF，并清理帧目录。

用法（先跑 Unity 侧渲染）：
    "F:/Unity/2022.3.62f1c1/Editor/Unity.exe" -batchmode -quit \
        -projectPath "F:/VSCode/transparent-pet-unity/transparent-pet" \
        -executeMethod TransparentPet.EditorTools.ProductShots.CaptureHeadless
    python tools/make_gif.py

输出：docs/images/breathe.gif、docs/images/throw.gif（hero.png 由 Unity 侧直接输出）
"""
import glob
import os
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
IMG_DIR = os.path.join(ROOT, "docs", "images")

# (帧目录, 输出文件, 每帧时长 ms, 调色板色数)
JOBS = [
    ("_frames_breathe", "breathe.gif", 50, 96),
    ("_frames_throw", "throw.gif", 33, 128),
]


def build_gif(src_dir, out_name, duration_ms, colors):
    frames = sorted(glob.glob(os.path.join(IMG_DIR, src_dir, "frame_*.png")))
    if not frames:
        print("skip (no frames):", src_dir)
        return

    images = [Image.open(f).convert("RGB") for f in frames]
    palette = images[len(images) // 2].quantize(colors=colors, method=Image.MEDIANCUT)
    frames_p = [im.quantize(palette=palette, dither=Image.NONE) for im in images]

    out_path = os.path.join(IMG_DIR, out_name)
    frames_p[0].save(out_path, save_all=True, append_images=frames_p[1:],
                     duration=duration_ms, loop=0, optimize=True, disposal=1)
    print("saved %s  frames=%d  %.0f KB" % (out_path, len(frames_p), os.path.getsize(out_path) / 1024))

    for f in frames:
        os.remove(f)
    os.rmdir(os.path.join(IMG_DIR, src_dir))


def main():
    for job in JOBS:
        build_gif(*job)


if __name__ == "__main__":
    main()
