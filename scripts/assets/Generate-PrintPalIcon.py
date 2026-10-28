from PIL import Image, ImageDraw

SIZE = 1024
BLUE = (37, 99, 235, 255)
BLUE_DARK = (29, 78, 216, 255)
WHITE = (255, 255, 255, 255)
LINE = (37, 99, 235, 255)
GREEN = (16, 185, 129, 255)

img = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Rounded blue tile
d.rounded_rectangle((24, 24, SIZE - 24, SIZE - 24), radius=210, fill=BLUE)
# Slight darker footer for depth
d.rounded_rectangle((24, 620, SIZE - 24, SIZE - 24), radius=210, fill=BLUE_DARK)
d.rectangle((24, 620, SIZE - 24, 830), fill=BLUE_DARK)
d.rounded_rectangle((24, 500, SIZE - 24, SIZE - 24), radius=210, fill=BLUE)

# Paper entering the printer
d.rounded_rectangle((286, 150, 738, 500), radius=34, fill=WHITE)
for y in (245, 315, 385):
    d.rounded_rectangle((370, y, 654, y + 28), radius=14, fill=LINE if y != 385 else (147, 197, 253, 255))

# Printer body
d.rounded_rectangle((168, 390, 856, 742), radius=56, fill=WHITE)

# Paper slot
d.rounded_rectangle((270, 470, 754, 530), radius=24, fill=BLUE_DARK)

# Output paper
d.rounded_rectangle((300, 620, 724, 930), radius=30, fill=WHITE)
for y in (700, 765, 830):
    d.rounded_rectangle((374, y, 650, y + 24), radius=12, fill=(191, 219, 254, 255))

# Status light
d.ellipse((760, 584, 820, 644), fill=GREEN)

# Front edge detail
d.rounded_rectangle((230, 690, 794, 716), radius=13, fill=(226, 232, 240, 255))

img.save("PrintPal.png")
sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
img.save("PrintPal.ico", sizes=[(s, s) for s in sizes])
print("wrote PrintPal.ico", img.size)
