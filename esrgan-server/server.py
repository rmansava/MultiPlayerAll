"""
Real-ESRGAN API server for image super-resolution.
Run: python server.py
Listens on port 5111
"""
import io
import os
import sys
import torch
import numpy as np
from PIL import Image
from flask import Flask, request, send_file, jsonify
from realesrgan import RealESRGANer
from basicsr.archs.rrdbnet_arch import RRDBNet

app = Flask(__name__)
upsampler = None

def get_upsampler():
    global upsampler
    if upsampler is None:
        print("Loading Real-ESRGAN model (RealESRGAN_x4plus)...")
        model = RRDBNet(num_in_ch=3, num_out_ch=3, num_feat=64, num_block=23, num_grow_ch=32, scale=4)
        upsampler = RealESRGANer(
            scale=4,
            model_path=None,  # auto-downloads
            dni_weight=None,
            model=model,
            tile=256,  # tile size for GPU memory management
            tile_pad=10,
            pre_pad=0,
            half=True,  # fp16 for speed
            gpu_id=0
        )
        # Download model if not present
        model_path = os.path.join(os.path.expanduser('~'), '.cache', 'realesrgan', 'RealESRGAN_x4plus.pth')
        if not os.path.exists(model_path):
            print("Downloading model weights...")
            from basicsr.utils.download_util import load_file_from_url
            os.makedirs(os.path.dirname(model_path), exist_ok=True)
            load_file_from_url(
                'https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth',
                model_dir=os.path.dirname(model_path)
            )
        upsampler.model_path = model_path
        upsampler = RealESRGANer(
            scale=4,
            model_path=model_path,
            dni_weight=None,
            model=model,
            tile=256,
            tile_pad=10,
            pre_pad=0,
            half=True,
            gpu_id=0
        )
        print("Model loaded!")
    return upsampler

@app.route('/enhance', methods=['POST'])
def enhance():
    if 'image' not in request.files:
        return jsonify({"error": "No image file provided"}), 400

    file = request.files['image']
    scale = int(request.form.get('scale', 4))

    try:
        # Read image
        img = Image.open(file.stream).convert('RGB')
        img_np = np.array(img)

        # BGR for OpenCV/Real-ESRGAN
        img_bgr = img_np[:, :, ::-1]

        # Enhance
        sr = get_upsampler()
        output, _ = sr.enhance(img_bgr, outscale=scale)

        # Convert back to RGB PIL
        output_rgb = output[:, :, ::-1]
        result = Image.fromarray(output_rgb)

        # Return as PNG
        buf = io.BytesIO()
        result.save(buf, format='PNG')
        buf.seek(0)

        return send_file(buf, mimetype='image/png')

    except Exception as e:
        return jsonify({"error": str(e)}), 500

@app.route('/health', methods=['GET'])
def health():
    return jsonify({"status": "ok", "gpu": torch.cuda.is_available()})

if __name__ == '__main__':
    # Pre-load model on startup
    get_upsampler()
    app.run(host='0.0.0.0', port=5111, threaded=True)
