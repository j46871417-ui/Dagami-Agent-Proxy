"""Reproduce the Dagami neon D icon with Python's standard library; no fonts or downloads."""
from pathlib import Path
import math
import struct
import zlib

ROOT = Path(__file__).resolve().parent / 'devproxy_pkg' / 'assets'

def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))

def line_dist(x, y, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(x - ax - t * dx, y - ay - t * dy)

def dist_d_seamless(x, y):
    d_spine = line_dist(x, y, 20, 16, 20, 48)
    if x >= 32:
        return abs(math.hypot(x - 32, y - 32) - 16)
    else:
        d_top = line_dist(x, y, 20, 16, 32, 16)
        d_bot = line_dist(x, y, 20, 48, 32, 48)
        return min(d_spine, d_top, d_bot)

def render(size):
    scale = size / 64.0
    rows = bytearray()
    for row in range(size):
        rows.append(0)
        y = (row + 0.5) / scale
        for col in range(size):
            x = (col + 0.5) / scale
            
            # Squircle card: 56x56 centered at (32, 32), r=13
            qx, qy = abs(x - 32) - 15, abs(y - 32) - 15
            card_dist = math.hypot(max(qx, 0), max(qy, 0)) + min(max(qx, qy), 0) - 13
            alpha = max(0.0, min(1.0, 0.5 - card_dist * scale))
            
            # Deep dark background with subtle blue vignetting
            d_center = math.hypot(x - 32, y - 32)
            bg_r = 10.0 + 3.0 * max(0.0, 1.0 - d_center / 36.0)
            bg_g = 14.0 + 7.0 * max(0.0, 1.0 - d_center / 36.0)
            bg_b = 26.0 + 15.0 * max(0.0, 1.0 - d_center / 36.0)
            
            d_tube = dist_d_seamless(x, y)
            
            # Neon wide bloom
            glow_wide = math.exp(-max(0.0, d_tube - 2.0) / 4.8) * 0.45
            # Neon dense bloom
            glow_dense = math.exp(-max(0.0, d_tube - 1.6) / 2.0) * 0.90
            # Neon electric cyan tube body
            tube_body = max(0.0, min(1.0, 0.5 - (d_tube - 2.5) * scale))
            # Neon white core
            tube_core = max(0.0, min(1.0, 0.5 - (d_tube - 1.1) * scale))
            
            r = bg_r + 0.0 * glow_wide + 15.0 * glow_dense
            g = bg_g + 170.0 * glow_wide + 225.0 * glow_dense
            b = bg_b + 255.0 * glow_wide + 255.0 * glow_dense
            
            if tube_body > 0:
                cyan_r = 10.0
                cyan_g = 240.0
                cyan_b = 255.0
                r = r * (1.0 - tube_body) + cyan_r * tube_body
                g = g * (1.0 - tube_body) + cyan_g * tube_body
                b = b * (1.0 - tube_body) + cyan_b * tube_body
                
            if tube_core > 0:
                r = r * (1.0 - tube_core) + 255.0 * tube_core
                g = g * (1.0 - tube_core) + 255.0 * tube_core
                b = b * (1.0 - tube_core) + 255.0 * tube_core
                
            r = min(255.0, max(0.0, r))
            g = min(255.0, max(0.0, g))
            b = min(255.0, max(0.0, b))
            
            rows.extend([round(r), round(g), round(b), round(alpha * 255)])
            
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows, 9)) + chunk(b'IEND', b'')

def main():
    ROOT.mkdir(parents=True, exist_ok=True)
    svg = '''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
  <title>Dagami Agent Proxy</title>
  <defs>
    <filter id="neon-glow" x="-30%" y="-30%" width="160%" height="160%">
      <feGaussianBlur stdDeviation="3.5" result="glow1"/>
      <feGaussianBlur stdDeviation="1.5" result="glow2"/>
      <feMerge>
        <feMergeNode in="glow1"/>
        <feMergeNode in="glow2"/>
        <feMergeNode in="SourceGraphic"/>
      </feMerge>
    </filter>
  </defs>
  <rect x="4" y="4" width="56" height="56" rx="13" fill="#0b0f1a"/>
  <path d="M 20 48 V 16 H 32 A 16 16 0 0 1 48 32 A 16 16 0 0 1 32 48 Z" fill="none" stroke="#00f0ff" stroke-width="5" stroke-linecap="round" stroke-linejoin="round" filter="url(#neon-glow)"/>
  <path d="M 20 48 V 16 H 32 A 16 16 0 0 1 48 32 A 16 16 0 0 1 32 48 Z" fill="none" stroke="#ffffff" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
'''
    (ROOT / 'dagami-agent-proxy.svg').write_text(svg, encoding='utf-8')
    images = {size: render(size) for size in (16, 24, 32, 48, 64, 128, 256, 512, 1024)}
    for size, data in images.items():
        (ROOT / f'icon-{size}.png').write_bytes(data)
    sizes = (16, 24, 32, 48, 64, 128, 256)
    directory = bytearray(struct.pack('<HHH', 0, 1, len(sizes)))
    offset = 6 + 16 * len(sizes)
    for size in sizes:
        data = images[size]
        directory.extend(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset))
        offset += len(data)
    (ROOT / 'dagami-agent-proxy.ico').write_bytes(directory + b''.join(images[s] for s in sizes))
    data = b''.join(kind + struct.pack('>I', len(images[size]) + 8) + images[size] for kind, size in ((b'ic07', 128), (b'ic08', 256), (b'ic09', 512), (b'ic10', 1024)))
    (ROOT / 'dagami-agent-proxy.icns').write_bytes(b'icns' + struct.pack('>I', len(data) + 8) + data)
    print(ROOT)

if __name__ == '__main__':
    main()
