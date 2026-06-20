from PIL import Image, ImageDraw
import os

def make_icon(size):
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # Фиолетово-синий градиентный квадрат (имитация)
    s = size
    bg_color = (91, 141, 239, 255)  # var(--accent) синий

    # Скруглённый фон
    radius = int(s * 0.22)
    # Простой квадрат, иконка небольшая - скругление через mask
    d.rounded_rectangle([0, 0, s-1, s-1], radius=radius, fill=bg_color)

    # Палитра: три цветных круга
    colors = [
        (232, 75, 74, 255),   # красный
        (29, 158, 117, 255),  # зелёный
        (186, 117, 23, 255),  # оранжевый
    ]
    r = int(s * 0.14)
    cx1 = int(s * 0.32)
    cy1 = int(s * 0.35)
    cx2 = int(s * 0.68)
    cy2 = int(s * 0.35)
    cx3 = int(s * 0.50)
    cy3 = int(s * 0.62)

    d.ellipse([cx1-r, cy1-r, cx1+r, cy1+r], fill=colors[0])
    d.ellipse([cx2-r, cy2-r, cx2+r, cy2+r], fill=colors[1])
    d.ellipse([cx3-r, cy3-r, cx3+r, cy3+r], fill=colors[2])

    # Кисть: диагональная линия
    brush_w = max(2, int(s * 0.04))
    bx1 = int(s * 0.22)
    by1 = int(s * 0.82)
    bx2 = int(s * 0.78)
    by2 = int(s * 0.22)
    # Ручка кисти - белая линия
    d.line([bx1, by1, bx2-int(s*0.15), by2+int(s*0.15)], fill=(255,255,255,255), width=brush_w)
    # Кончик кисти - тёмный
    d.ellipse([bx2-int(s*0.11), by2-int(s*0.02), bx2+int(s*0.05), by2+int(s*0.14)], fill=(44,44,58,255))

    return img

out_dir = '/home/claude/paint-pro-electron/build'
os.makedirs(out_dir, exist_ok=True)

# Сохраняем ICO - правильный способ: сохраняем самую большую, PIL сам смасштабирует
largest = make_icon(256)
largest.save(
    os.path.join(out_dir, 'icon.ico'),
    format='ICO',
    sizes=[(16,16), (24,24), (32,32), (48,48), (64,64), (128,128), (256,256)]
)

# Дополнительно PNG 512px для других платформ
make_icon(512).save(os.path.join(out_dir, 'icon.png'))

print('Иконка создана:', os.path.join(out_dir, 'icon.ico'))
