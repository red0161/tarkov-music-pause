"""Generate icon.ico from the tray image — run once before building."""
from PIL import Image, ImageDraw


def make_image(size: int) -> Image.Image:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    draw.ellipse([0, 0, size - 1, size - 1], fill=(20, 20, 20, 255))
    s = size / 64
    # Play triangle
    draw.polygon([
        (round(10*s), round(16*s)),
        (round(10*s), round(48*s)),
        (round(30*s), round(32*s)),
    ], fill=(255, 255, 255, 255))
    # Pause bars
    draw.rectangle([round(34*s), round(16*s), round(43*s), round(48*s)], fill=(255, 255, 255, 255))
    draw.rectangle([round(47*s), round(16*s), round(56*s), round(48*s)], fill=(255, 255, 255, 255))
    return img


sizes = [16, 32, 48, 64, 128, 256]
images = [make_image(s) for s in sizes]
images[0].save("icon.ico", format="ICO", sizes=[(s, s) for s in sizes], append_images=images[1:])
print("icon.ico written")
