// <copyright file="aidebug-map.js" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

/**
 * Decodes a base64 string to a Uint8Array.
 */
function base64ToBytes(base64) {
    if (!base64) return new Uint8Array(0);
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
}

/**
 * Renders a MU terrain grid onto an HTML5 canvas.
 * @param {string} canvasId - The canvas element ID.
 * @param {string|Uint8Array|Array} terrainData - Raw .att terrain data (base64 string, Uint8Array, or number array).
 * @param {number} mapSize - Grid size (default 256).
 * @param {number} offsetX - Viewport center X (player position).
 * @param {number} offsetY - Viewport center Y (player position).
 * @param {number} viewRadius - Half viewport size in tiles (default 30).
 * @param {Array} entities - Array of {x, y, type, color, label} objects.
 */
window.renderAiDebugMap = function (canvasId, terrainData, mapSize, offsetX, offsetY, viewRadius, entities) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;

    const ctx = canvas.getContext('2d');
    const width = canvas.width;
    const height = canvas.height;

    // Decode terrain data
    let bytes;
    if (typeof terrainData === 'string') {
        bytes = base64ToBytes(terrainData);
    } else if (terrainData instanceof Uint8Array || Array.isArray(terrainData)) {
        bytes = terrainData;
    } else {
        bytes = new Uint8Array(0);
    }

    const size = mapSize || 256;
    const halfView = viewRadius || 30;
    const tileSize = Math.floor(Math.min(width, height) / (halfView * 2));
    if (tileSize < 1) return;

    const startX = Math.max(0, offsetX - halfView);
    const startY = Math.max(0, offsetY - halfView);
    const endX = Math.min(size, offsetX + halfView);
    const endY = Math.min(size, offsetY + halfView);

    // Clear
    ctx.fillStyle = '#1a1a2e';
    ctx.fillRect(0, 0, width, height);

    // Draw terrain tiles
    for (let ty = startY; ty < endY; ty++) {
        for (let tx = startX; tx < endX; tx++) {
            const idx = ty * size + tx;
            const att = idx < bytes.length ? bytes[idx] : 0;
            const terrainType = att & 0x0F;
            const isSafeZone = (att & 0x20) !== 0;
            const isDamageZone = (att & 0x40) !== 0;

            let color;
            if (isSafeZone) {
                color = '#2d5a8a';
            } else if (isDamageZone) {
                color = '#8a2d2d';
            } else {
                switch (terrainType) {
                    case 0: color = '#3a5a3a'; break;
                    case 1: color = '#4a6a3a'; break;
                    case 2: color = '#5a5a4a'; break;
                    case 3: color = '#2a4a6a'; break;
                    case 4: color = '#6a5a3a'; break;
                    case 5: color = '#6a6a6a'; break;
                    default: color = '#2a2a2a'; break;
                }
            }

            const px = (tx - startX) * tileSize;
            const py = (ty - startY) * tileSize;
            ctx.fillStyle = color;
            ctx.fillRect(px, py, tileSize, tileSize);
        }
    }

    // Draw entities
    if (entities && entities.length > 0) {
        for (const e of entities) {
            const ex = e.x - startX;
            const ey = e.y - startY;
            if (ex < 0 || ex >= (endX - startX) || ey < 0 || ey >= (endY - startY)) continue;

            const cx = ex * tileSize + tileSize / 2;
            const cy = ey * tileSize + tileSize / 2;
            const radius = e.type === 'player' ? Math.max(4, tileSize / 2) : Math.max(2, tileSize / 3);

            ctx.beginPath();
            ctx.arc(cx, cy, radius, 0, Math.PI * 2);
            ctx.fillStyle = e.color || (e.type === 'player' ? '#ff4444' : '#aaaaaa');
            ctx.fill();
            ctx.strokeStyle = '#ffffff';
            ctx.lineWidth = 1;
            ctx.stroke();

            if (e.label) {
                ctx.fillStyle = '#ffffff';
                ctx.font = '10px sans-serif';
                ctx.fillText(e.label, cx + radius + 2, cy + 4);
            }
        }
    }

    // Grid lines
    ctx.strokeStyle = 'rgba(255,255,255,0.05)';
    ctx.lineWidth = 0.5;
    const tilesX = endX - startX;
    const tilesY = endY - startY;
    for (let i = 0; i <= tilesX; i++) {
        ctx.beginPath();
        ctx.moveTo(i * tileSize, 0);
        ctx.lineTo(i * tileSize, tilesY * tileSize);
        ctx.stroke();
    }
    for (let i = 0; i <= tilesY; i++) {
        ctx.beginPath();
        ctx.moveTo(0, i * tileSize);
        ctx.lineTo(tilesX * tileSize, i * tileSize);
        ctx.stroke();
    }
};
