"""
Real-ESRGAN CLI: python enhance_cli.py input.png output.png [scale]
Called by TriviaCommunication as a subprocess. No server needed.
"""
import sys
import os
import numpy as np
from PIL import Image
from realesrgan import RealESRGANer
from basicsr.archs.rrdbnet_arch import RRDBNet

def main():
    if len(sys.argv) < 3:
        print("Usage: python enhance_cli.py input.png output.png [scale]", file=sys.stderr)
        sys.exit(1)

    input_path = sys.argv[1]
    output_path = sys.argv[2]
    scale = int(sys.argv[3]) if len(sys.argv) > 3 else 4

    # Model path
    model_dir = os.path.join(os.path.expanduser('~'), '.cache', 'realesrgan')
    model_path = os.path.join(model_dir, 'RealESRGAN_x4plus.pth')

    # Auto-download model if not present
    if not os.path.exists(model_path):
        print("Downloading Real-ESRGAN model...", file=sys.stderr)
        os.makedirs(model_dir, exist_ok=True)
        from basicsr.utils.download_util import load_file_from_url
        load_file_from_url(
            'https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth',
            model_dir=model_dir
        )

    model = RRDBNet(num_in_ch=3, num_out_ch=3, num_feat=64, num_block=23, num_grow_ch=32, scale=4)
    upsampler = RealESRGANer(
        scale=4,
        model_path=model_path,
        model=model,
        tile=256,
        tile_pad=10,
        pre_pad=0,
        half=True,
        gpu_id=0
    )

    img = Image.open(input_path).convert('RGB')
    img_bgr = np.array(img)[:, :, ::-1]

    output, _ = upsampler.enhance(img_bgr, outscale=scale)

    output_rgb = output[:, :, ::-1]
    Image.fromarray(output_rgb).save(output_path)
    print(f"Enhanced {input_path} -> {output_path} ({output.shape[1]}x{output.shape[0]})")

if __name__ == '__main__':
    main()
