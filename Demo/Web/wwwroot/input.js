// Movement input: keyboard (WASD / arrows) and a floating virtual joystick on touch.

const KEYS = {
    KeyW: [0, 1], ArrowUp: [0, 1],
    KeyS: [0, -1], ArrowDown: [0, -1],
    KeyA: [-1, 0], ArrowLeft: [-1, 0],
    KeyD: [1, 0], ArrowRight: [1, 0],
};

export class Input {
    constructor(surface, stickEl, knobEl) {
        this.down = new Set();
        this.fresh = new Set();     // pressed since the last read()
        this.released = new Set();  // released before read() saw them: apply after one frame
        this.stick = { id: null, ox: 0, oy: 0, x: 0, y: 0 };
        this.stickEl = stickEl;
        this.knobEl = knobEl;
        this.radius = 56;

        window.addEventListener('keydown', e => {
            if (KEYS[e.code]) {
                if (!this.down.has(e.code)) this.fresh.add(e.code);
                this.down.add(e.code);
                this.released.delete(e.code);
                e.preventDefault();
            }
        });
        window.addEventListener('keyup', e => {
            // A tap shorter than a frame still moves the player for one frame.
            if (this.fresh.has(e.code)) this.released.add(e.code);
            else this.down.delete(e.code);
        });
        window.addEventListener('blur', () => this.clear());

        surface.addEventListener('pointerdown', e => {
            if (e.pointerType === 'mouse' || this.stick.id !== null) return;
            this.stick.id = e.pointerId;
            this.stick.ox = e.clientX;
            this.stick.oy = e.clientY;
            this.stick.x = this.stick.y = 0;
            surface.setPointerCapture?.(e.pointerId);
            this.stickEl.style.left = `${e.clientX}px`;
            this.stickEl.style.top = `${e.clientY}px`;
            this.stickEl.classList.remove('hidden');
            this.moveKnob(0, 0);
            e.preventDefault();
        });
        surface.addEventListener('pointermove', e => {
            if (e.pointerId !== this.stick.id) return;
            let dx = e.clientX - this.stick.ox, dy = e.clientY - this.stick.oy;
            const len = Math.hypot(dx, dy);
            if (len > this.radius) {
                // drag the base along so reversing direction is instant
                const k = (len - this.radius) / len;
                this.stick.ox += dx * k;
                this.stick.oy += dy * k;
                this.stickEl.style.left = `${this.stick.ox}px`;
                this.stickEl.style.top = `${this.stick.oy}px`;
                dx -= dx * k;
                dy -= dy * k;
            }
            this.stick.x = dx / this.radius;
            this.stick.y = -dy / this.radius;
            this.moveKnob(dx, dy);
            e.preventDefault();
        });
        const end = e => {
            if (e.pointerId !== this.stick.id) return;
            this.stick.id = null;
            this.stick.x = this.stick.y = 0;
            this.stickEl.classList.add('hidden');
        };
        surface.addEventListener('pointerup', end);
        surface.addEventListener('pointercancel', end);
    }

    moveKnob(dx, dy) {
        this.knobEl.style.transform = `translate(calc(-50% + ${dx}px), calc(-50% + ${dy}px))`;
    }

    /** Current direction; the game normalizes vectors longer than 1. */
    read() {
        let x = 0, y = 0;
        for (const code of this.down) {
            const k = KEYS[code];
            x += k[0];
            y += k[1];
        }
        this.fresh.clear();
        for (const code of this.released) this.down.delete(code);
        this.released.clear();
        x = Math.max(-1, Math.min(1, x));
        y = Math.max(-1, Math.min(1, y));
        if (this.stick.id !== null) {
            const dead = Math.hypot(this.stick.x, this.stick.y) < 0.12;
            if (!dead) { x += this.stick.x; y += this.stick.y; }
        }
        return [x, y];
    }

    clear() {
        this.down.clear();
        this.fresh.clear();
        this.released.clear();
    }
}
