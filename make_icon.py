"""Generate icon.ico from the tray image — run once before building."""
from PIL import Image, ImageDraw


def make_image(size: int) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    draw.ellipse([0, 0, size - 1, size - 1], fill=(30, 30, 30, 255))
    s = size / 64
    # T top bar
    draw.rectangle([round(10*s), round(12*s), round(54*s), round(24*s)], fill=(255, 255, 255, 255))
    # Split stem = pause symbol (two bars)
    draw.rectangle([round(20*s), round(26*s), round(30*s), round(52*s)], fill=(255, 255, 255, 255))
    draw.rectangle([round(34*s), round(26*s), round(44*s), round(52*s)], fill=(255, 255, 255, 255))
    return img


sizes = [16, 32, 48, 64, 128, 256]
images = [make_image(s) for s in sizes]
images[0].save("icon.ico", format="ICO", sizes=[(s, s) for s in sizes], append_images=images[1:])
print("icon.ico written")
