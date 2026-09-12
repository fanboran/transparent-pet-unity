# -*- coding: utf-8 -*-
"""从 PetSlime.png 生成应用图标位图（exe 图标 / 托盘图标素材）。

用法：python tools/make_icon.py
输出：transparent-pet/Assets/Art/Icon/AppIcon_{16,32,48,64,128,256}.png
（产物入库；Unity 侧由 EditorTools.AppIconSetup 导入并设为 Standalone 图标）
"""
import os
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "transparent-pet", "Assets", "Art", "Pet", "PetSlime.png")
OUT_DIR = os.path.join(ROOT, "transparent-pet", "Assets", "Art", "Icon")
SIZES = (256, 128, 64, 48, 32, 16)
CONTENT_RATIO = 0.86  # 内容占画布比例（留边，任务栏/资源管理器里不贴边）

def main():
    src = Image.open(SRC).convert("RGBA")
    bbox = src.getchannel("A").getbbox()  # 按 alpha 裁掉透明留白
    slime = src.crop(bbox)
    w, h = slime.size
    os.makedirs(OUT_DIR, exist_ok=True)

    for size in SIZES:
        canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        scale = (size * CONTENT_RATIO) / max(w, h)
        nw, nh = max(1, round(w * scale)), max(1, round(h * scale))
        resized = slime.resize((nw, nh), Image.LANCZOS)
        canvas.paste(resized, ((size - nw) // 2, (size - nh) // 2), resized)
        path = os.path.join(OUT_DIR, "AppIcon_%d.png" % size)
        canvas.save(path)
        print("saved", path)

    print("source alpha bbox =", bbox, "slime size =", slime.size)

if __name__ == "__main__":
    main()
