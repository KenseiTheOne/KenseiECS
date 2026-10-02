// WebGL2 renderer for the Horde sprite buffer.
//
// One instanced draw call for all sprites: each instance is 5 floats straight from
// the .NET sprite buffer (x, y, radius, kind, flash) — uploaded with a single
// bufferSubData from a view over wasm memory, no per-sprite JS work. Shapes are
// signed-distance functions in the fragment shader, picked by `kind`.
// Blending is premultiplied alpha; glow is written with alpha 0, which makes it
// additive under the same blend function (no second pass).

const STRIDE = 5;
const BYTES = STRIDE * 4;

const SPRITE_VS = `#version 300 es
layout(location = 0) in vec2 a_corner;
layout(location = 1) in vec4 a_inst;   // x, y, radius, kind
layout(location = 2) in float a_flash;
uniform vec2 u_cam;
uniform vec2 u_scale;      // world units -> clip space
uniform float u_pxPerUnit; // device pixels per world unit
out vec2 v_local;          // position in units of the sprite radius
out float v_flash;
out float v_r;
out float v_aa;            // one device pixel, in units of the radius
flat out int v_kind;
flat out float v_seed;

void main() {
    int kind = int(a_inst.w + 0.5);
    float r = max(a_inst.z, 0.01);
    // Quad half-size in radius units: room for the glow halo around the shape.
    float pad = 1.35;
    if (kind == 0) pad = 2.6;
    else if (kind == 4) pad = 1.9;
    else if (kind == 5) pad = 3.4;
    else if (kind == 6) pad = 1.9;
    else if (kind == 7) pad = 1.0 + 2.2 / r;   // keep in sync with pad in the nova branch of the fragment shader
    else if (kind == 8 || kind == 9) pad = 2.4;
    else if (kind == 10) pad = 2.6;
    vec2 local = a_corner * pad;
    vec2 world = a_inst.xy + local * r;
    gl_Position = vec4((world - u_cam) * u_scale, 0.0, 1.0);
    v_local = local;
    v_flash = a_flash;
    v_r = r;
    v_aa = 1.0 / max(r * u_pxPerUnit, 0.5);
    v_kind = kind;
    v_seed = fract(sin(dot(a_inst.xy, vec2(12.9898, 78.233))) * 43758.5453);
}`;

const SPRITE_FS = `#version 300 es
precision highp float;
in vec2 v_local;
in float v_flash;
in float v_r;
in float v_aa;
flat in int v_kind;
flat in float v_seed;
uniform float u_time;
out vec4 o_color;

float sdBox(vec2 p, vec2 b, float rr) {
    vec2 q = abs(p) - b + rr;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - rr;
}
float sdHex(vec2 p, float r) {
    const vec3 k = vec3(-0.866025404, 0.5, 0.577350269);
    p = abs(p);
    p -= 2.0 * min(dot(k.xy, p), 0.0) * k.xy;
    p -= vec2(clamp(p.x, -k.z * r, k.z * r), r);
    return length(p) * sign(p.y);
}
mat2 rot(float a) { float c = cos(a), s = sin(a); return mat2(c, -s, s, c); }

// Fill + rim + halo, premultiplied. Halo goes out with alpha 0 (additive).
vec4 shade(float s, vec3 fill, vec3 rim, float rimW, vec3 glow, float glowAmt, float glowFall) {
    float aa = v_aa * 1.2;
    float inside = 1.0 - smoothstep(-aa, aa, s);
    float rimMask = smoothstep(-rimW - aa, -rimW + aa, s);
    vec3 col = mix(fill, rim, rimMask);
    float halo = glowAmt * exp(-max(s, 0.0) * glowFall) * (1.0 - inside);
    return vec4(col * inside + glow * halo, inside);
}

void main() {
    vec2 p = v_local;
    float d = length(p);
    vec4 c = vec4(0.0);
    float flash = clamp(v_flash, 0.0, 1.0);

    if (v_kind == 0) {                       // player
        float s = d - 1.0;
        float pulse = 0.5 + 0.5 * sin(u_time * 5.0);
        vec3 core = mix(vec3(0.80, 0.88, 1.0), vec3(1.0), smoothstep(0.6, 0.0, d));
        c = shade(s, core, vec3(0.36, 0.45, 0.98), 0.28, vec3(0.35, 0.45, 1.0), 0.55 + 0.15 * pulse, 2.2);
        c.rgb = mix(c.rgb, vec3(1.0, 0.35, 0.35) * c.a, flash);
    } else if (v_kind >= 1 && v_kind <= 4) { // enemies
        float s;
        vec3 fill, rim, glow;
        float glowAmt = 0.10;
        if (v_kind == 1) {                   // basic: round blob
            s = d - 1.0;
            fill = vec3(0.90, 0.28, 0.33); rim = vec3(0.45, 0.08, 0.14); glow = vec3(0.9, 0.2, 0.25);
        } else if (v_kind == 2) {            // fast: diamond
            vec2 q = abs(p);
            s = (q.x * 0.8 + q.y) * 0.75 - 0.78;
            fill = vec3(1.0, 0.66, 0.20); rim = vec3(0.55, 0.28, 0.04); glow = vec3(1.0, 0.55, 0.1);
        } else if (v_kind == 3) {            // tank: rounded square
            s = sdBox(p, vec2(0.86), 0.32);
            fill = vec3(0.62, 0.36, 0.92); rim = vec3(0.26, 0.12, 0.45); glow = vec3(0.6, 0.3, 1.0);
        } else {                             // elite: rotating hexagon, pulsing aura
            vec2 q = rot(u_time * 0.8 + v_seed * 6.28) * p;
            s = sdHex(q, 0.88);
            float pulse = 0.5 + 0.5 * sin(u_time * 6.0 + v_seed * 10.0);
            fill = mix(vec3(1.0, 0.30, 0.75), vec3(1.0, 0.75, 0.95), smoothstep(0.5, 0.0, d));
            rim = vec3(0.45, 0.05, 0.30); glow = vec3(1.0, 0.25, 0.7);
            glowAmt = 0.45 + 0.25 * pulse;
        }
        // soft top-left light so crowds read as volumes, not flat discs
        fill *= 0.82 + 0.3 * clamp(0.6 - dot(p, vec2(0.45, -0.55)) * 0.6, 0.0, 1.0);
        c = shade(s, fill, rim, 0.22, glow, glowAmt, 4.0);
        c.rgb = mix(c.rgb, vec3(1.0) * c.a, flash * 0.85);
    } else if (v_kind == 5) {                // bolt
        float s = d - 1.0;
        c = shade(s, vec3(1.0, 0.98, 0.85), vec3(1.0, 0.85, 0.4), 0.3, vec3(1.0, 0.8, 0.35), 0.9, 1.6);
    } else if (v_kind == 6) {                // blade: spinning shuriken
        vec2 q = rot(u_time * 9.0) * p;
        float a = atan(q.y, q.x);
        float shape = 0.42 + 0.58 * pow(abs(cos(a * 2.0)), 6.0);
        float s = (d - shape) * 0.8;
        c = shade(s, vec3(0.78, 0.98, 1.0), vec3(0.25, 0.80, 0.95), 0.18, vec3(0.3, 0.85, 1.0), 0.6, 3.0);
    } else if (v_kind == 7) {                // nova ring: radius = ring, flash = alpha
        float thick = 0.32 / v_r;
        float s = abs(d - 1.0) - thick;
        float alpha = flash;
        float inside = 1.0 - smoothstep(-v_aa, v_aa, s);
        // Fade the halo out before the circle inscribed in the quad, so the quad's
        // straight edges never show (they did while the ring was small).
        float pad = 1.0 + 2.2 / v_r;
        float window = 1.0 - smoothstep(pad - 1.2 / v_r, pad, d);
        float halo = exp(-max(s, 0.0) * v_r * 1.8) * (1.0 - inside) * window;
        float fillIn = 0.07 * smoothstep(1.0, 0.0, d) * step(d, 1.0);
        vec3 col = vec3(0.62, 0.70, 1.0);
        c = vec4(col * (inside + 0.55 * halo + fillIn) * alpha, inside * alpha * 0.8);
    } else if (v_kind == 8 || v_kind == 9) { // xp gems: bobbing diamonds
        float bob = 0.08 * sin(u_time * 4.0 + v_seed * 6.28);
        vec2 q = abs(p + vec2(0.0, bob));
        float s = (q.x * 1.35 + q.y) * 0.72 - 0.72;
        vec3 fill = v_kind == 8 ? vec3(0.35, 0.95, 0.55) : vec3(0.40, 0.75, 1.0);
        vec3 rim = v_kind == 8 ? vec3(0.10, 0.45, 0.25) : vec3(0.12, 0.30, 0.65);
        fill *= 0.85 + 0.35 * smoothstep(0.2, -0.6, p.x + p.y);
        c = shade(s, fill, rim, 0.22, fill, v_kind == 8 ? 0.35 : 0.55, 2.5);
    } else {                                 // spark: additive ember
        float t = flash;
        float g = exp(-d * d * 2.2);
        vec3 col = mix(vec3(1.0, 0.35, 0.1), vec3(1.0, 0.9, 0.6), t);
        c = vec4(col * g * t * 1.3, 0.0);
    }
    o_color = c;
}`;

const GRID_VS = `#version 300 es
const vec2 P[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
void main() { gl_Position = vec4(P[gl_VertexID], 0.0, 1.0); }`;

const GRID_FS = `#version 300 es
precision highp float;
uniform vec2 u_cam;
uniform vec2 u_res;
uniform float u_pxPerUnit;
out vec4 o_color;
float gridLine(vec2 w, float step, float px) {
    vec2 g = abs(fract(w / step - 0.5) - 0.5) * step;  // distance to nearest line, world units
    float d = min(g.x, g.y) * u_pxPerUnit;            // ... in pixels
    return 1.0 - smoothstep(px * 0.5, px * 0.5 + 1.0, d);
}
void main() {
    vec2 frag = gl_FragCoord.xy - u_res * 0.5;
    vec2 w = u_cam + frag / u_pxPerUnit;
    vec3 base = vec3(0.043, 0.051, 0.071);
    float minor = gridLine(w, 2.0, 1.0);
    float major = gridLine(w, 10.0, 1.5);
    vec3 col = base + vec3(0.035, 0.04, 0.06) * minor + vec3(0.06, 0.07, 0.12) * major;
    vec2 uv = frag / u_res;
    float vig = 1.0 - 0.55 * dot(uv, uv);
    o_color = vec4(col * vig, 1.0);
}`;

// Game-over look: the frame is drawn into a texture, then this pass bulges it like a
// fisheye lens (centre magnified, edges squeezed), splits the colour channels a little
// towards the edges and drains about half of the colour. u_amt eases 0 -> 1.
const POST_FS = `#version 300 es
precision highp float;
uniform sampler2D u_scene;
uniform vec2 u_res;
uniform float u_amt;
uniform float u_time;
out vec4 o_color;
// Samples p * (0.70 + k r^2): the centre is magnified ~1.4x and the scale grows
// towards the edges. The corners (r^2 = 2) land at <= 0.97 of the frame, so the
// lens never reaches outside the picture and no black corners appear.
vec2 lens(vec2 p, float k) {
    float r2 = dot(p, p);
    return p * mix(1.0, 0.70 + k * r2, u_amt);
}
void main() {
    vec2 uv = gl_FragCoord.xy / u_res;
    vec2 p = uv * 2.0 - 1.0;
    float k = 0.125 + 0.01 * sin(u_time * 1.6);   // corners: 0.70 + 2k <= 0.97
    float split = 0.014 * u_amt;
    vec2 g = lens(p, k);
    vec2 sr = g * (1.0 + split * dot(p, p)) * 0.5 + 0.5;
    vec2 sg = g * 0.5 + 0.5;
    vec2 sb = g * (1.0 - split * dot(p, p)) * 0.5 + 0.5;
    vec3 col = vec3(texture(u_scene, sr).r, texture(u_scene, sg).g, texture(u_scene, sb).b);
    float lum = dot(col, vec3(0.299, 0.587, 0.114));
    col = mix(col, vec3(lum), 0.45 * u_amt);
    col *= 1.0 - 0.25 * u_amt * smoothstep(0.5, 2.0, dot(p, p));
    o_color = vec4(col, 1.0);
}`;

function compile(gl, type, src) {
    const sh = gl.createShader(type);
    gl.shaderSource(sh, src);
    gl.compileShader(sh);
    if (!gl.getShaderParameter(sh, gl.COMPILE_STATUS)) {
        throw new Error('Shader compile failed: ' + gl.getShaderInfoLog(sh));
    }
    return sh;
}

function program(gl, vs, fs) {
    const p = gl.createProgram();
    gl.attachShader(p, compile(gl, gl.VERTEX_SHADER, vs));
    gl.attachShader(p, compile(gl, gl.FRAGMENT_SHADER, fs));
    gl.linkProgram(p);
    if (!gl.getProgramParameter(p, gl.LINK_STATUS)) {
        throw new Error('Program link failed: ' + gl.getProgramInfoLog(p));
    }
    const u = {};
    const n = gl.getProgramParameter(p, gl.ACTIVE_UNIFORMS);
    for (let i = 0; i < n; i++) {
        const name = gl.getActiveUniform(p, i).name;
        u[name] = gl.getUniformLocation(p, name);
    }
    return { p, u };
}

export function supportsWebGL2() {
    try {
        const c = document.createElement('canvas');
        return !!c.getContext('webgl2');
    } catch {
        return false;
    }
}

export class Renderer {
    constructor(canvas, viewHeight) {
        this.canvas = canvas;
        this.viewHeight = viewHeight;
        const gl = canvas.getContext('webgl2', {
            antialias: false, alpha: false, depth: false, stencil: false,
            premultipliedAlpha: true, powerPreference: 'high-performance', desynchronized: true
        });
        if (!gl) throw new Error('WebGL2 is not available');
        this.gl = gl;
        this.sprite = program(gl, SPRITE_VS, SPRITE_FS);
        this.grid = program(gl, GRID_VS, GRID_FS);
        this.post = program(gl, GRID_VS, POST_FS);
        this.target = null;      // { fbo, tex, w, h }, created on the first game-over frame
        this.deathAt = 0;        // performance.now() when the run ended; 0 while playing
        this.reducedMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;

        this.vao = gl.createVertexArray();
        gl.bindVertexArray(this.vao);
        const quad = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, quad);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
        gl.enableVertexAttribArray(0);
        gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);

        this.instances = gl.createBuffer();
        this.capacity = 0;
        gl.bindBuffer(gl.ARRAY_BUFFER, this.instances);
        this.ensureCapacity(8192);
        gl.enableVertexAttribArray(1);
        gl.vertexAttribPointer(1, 4, gl.FLOAT, false, BYTES, 0);
        gl.vertexAttribDivisor(1, 1);
        gl.enableVertexAttribArray(2);
        gl.vertexAttribPointer(2, 1, gl.FLOAT, false, BYTES, 16);
        gl.vertexAttribDivisor(2, 1);
        gl.bindVertexArray(null);

        this.emptyVao = gl.createVertexArray();
        this.count = 0;
        this.camX = 0;
        this.camY = 0;
        this.dpr = 1;
        this.resize();
        this._ro = new ResizeObserver(() => this.resize());
        this._ro.observe(canvas);

        canvas.addEventListener('webglcontextlost', e => { e.preventDefault(); this.lost = true; });
    }

    ensureCapacity(sprites) {
        if (sprites <= this.capacity) return;
        let cap = Math.max(this.capacity, 8192);
        while (cap < sprites) cap *= 2;
        const gl = this.gl;
        gl.bindBuffer(gl.ARRAY_BUFFER, this.instances);
        gl.bufferData(gl.ARRAY_BUFFER, cap * BYTES, gl.DYNAMIC_DRAW);
        this.capacity = cap;
    }

    resize() {
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        const w = Math.max(1, Math.round(this.canvas.clientWidth * dpr));
        const h = Math.max(1, Math.round(this.canvas.clientHeight * dpr));
        if (this.canvas.width !== w || this.canvas.height !== h) {
            this.canvas.width = w;
            this.canvas.height = h;
        }
        this.dpr = dpr;
    }

    /** Upload this frame's sprites. `floats` is a Float32Array view over wasm memory. */
    upload(floats, count, camX, camY) {
        const gl = this.gl;
        this.count = count;
        this.camX = camX;
        this.camY = camY;
        if (count === 0) return;
        this.ensureCapacity(count);
        gl.bindBuffer(gl.ARRAY_BUFFER, this.instances);
        gl.bufferSubData(gl.ARRAY_BUFFER, 0, floats, 0, count * STRIDE);
    }

    /** Start (or, with false, clear) the game-over lens effect. */
    setGameOver(on) {
        if (on && !this.deathAt) this.deathAt = performance.now();
        if (!on) this.deathAt = 0;
    }

    ensureTarget(w, h) {
        const gl = this.gl;
        if (this.target && this.target.w === w && this.target.h === h) return this.target;
        if (this.target) {
            gl.deleteFramebuffer(this.target.fbo);
            gl.deleteTexture(this.target.tex);
        }
        const tex = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        const fbo = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
        this.target = { fbo, tex, w, h };
        return this.target;
    }

    draw(timeSec) {
        if (this.lost) return;
        const gl = this.gl;
        const w = this.canvas.width, h = this.canvas.height;
        let amt = 0;
        if (this.deathAt) {
            const t = Math.min(1, (performance.now() - this.deathAt) / 900);
            amt = this.reducedMotion ? 1 : 1 - Math.pow(1 - t, 3);
        }
        const target = amt > 0 ? this.ensureTarget(w, h) : null;
        gl.bindFramebuffer(gl.FRAMEBUFFER, target ? target.fbo : null);
        gl.viewport(0, 0, w, h);
        const pxPerUnit = h / this.viewHeight;

        gl.disable(gl.BLEND);
        gl.useProgram(this.grid.p);
        gl.uniform2f(this.grid.u.u_cam, this.camX, this.camY);
        gl.uniform2f(this.grid.u.u_res, w, h);
        gl.uniform1f(this.grid.u.u_pxPerUnit, pxPerUnit);
        gl.bindVertexArray(this.emptyVao);
        gl.drawArrays(gl.TRIANGLES, 0, 3);

        if (this.count > 0) {
            gl.enable(gl.BLEND);
            gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
            gl.useProgram(this.sprite.p);
            gl.uniform2f(this.sprite.u.u_cam, this.camX, this.camY);
            gl.uniform2f(this.sprite.u.u_scale, 2 * pxPerUnit / w, 2 * pxPerUnit / h);
            gl.uniform1f(this.sprite.u.u_pxPerUnit, pxPerUnit);
            gl.uniform1f(this.sprite.u.u_time, timeSec);
            gl.bindVertexArray(this.vao);
            gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, this.count);
        }

        if (target) {
            gl.bindFramebuffer(gl.FRAMEBUFFER, null);
            gl.disable(gl.BLEND);
            gl.useProgram(this.post.p);
            gl.activeTexture(gl.TEXTURE0);
            gl.bindTexture(gl.TEXTURE_2D, target.tex);
            gl.uniform1i(this.post.u.u_scene, 0);
            gl.uniform2f(this.post.u.u_res, w, h);
            gl.uniform1f(this.post.u.u_amt, amt);
            gl.uniform1f(this.post.u.u_time, this.reducedMotion ? 0 : performance.now() / 1000);
            gl.bindVertexArray(this.emptyVao);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
        }
        gl.bindVertexArray(null);
    }
}
