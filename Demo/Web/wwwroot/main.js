// Boots .NET (WebAssembly), wires the JS side of the interop and runs the frame loop.
//
// Frame: requestAnimationFrame -> Interop.Tick(dt, input) in C# -> HordeGame.Tick
//        -> C# calls back render(view, ...) below with a view over the sprite buffer
//        -> one bufferSubData + one instanced draw.

import { dotnet } from './_framework/dotnet.js';
import { Renderer, supportsWebGL2 } from './renderer.js';
import { UI } from './ui.js';
import { Overlay } from './overlay.js';
import { Input } from './input.js';

const VIEW_HEIGHT = 36;       // HordeGame.ViewHeight
const STRIDE = 5;             // HordeGame.SpriteStride
const MAX_DT = 1 / 20;        // HordeGame clamps to this too

// Packed HUD written by Interop.Publish (Program.cs).
const HUD_HP = 0, HUD_MAXHP = 1, HUD_LEVEL = 2, HUD_XP = 3, HUD_XPNEXT = 4, HUD_TIME = 5, HUD_KILLS = 6,
    HUD_STATUS = 7, HUD_C0 = 8, HUD_C1 = 9, HUD_C2 = 10, HUD_ENEMIES = 11, HUD_FRAMEMS = 12, HUD_PEAK = 13;
const PLAYING = 0, LEVELUP = 1, GAMEOVER = 2;

const $ = id => document.getElementById(id);
const params = new URLSearchParams(location.search);
const embed = params.get('embed') === '1';
if (embed) document.body.classList.add('embed');

const loadingText = $('loading-text');
const progressFill = $('progress-fill');

function fail(message, detail) {
    $('loading').classList.add('error');
    loadingText.textContent = message;
    if (detail) {
        const pre = document.createElement('pre');
        pre.textContent = String(detail);
        loadingText.after(pre);
    }
}

async function main() {
    if (!supportsWebGL2()) {
        fail('This demo needs WebGL2, which this browser or device does not provide. ' +
            'Try a recent Chrome, Firefox, Safari or Edge with hardware acceleration enabled.');
        return;
    }

    const canvas = $('view');
    const renderer = new Renderer(canvas, VIEW_HEIGHT);

    // ---------------------------------------------------------------- boot .NET
    let shownProgress = 0;
    const runtime = await dotnet
        .withModuleConfig({
            onDownloadResourceProgress(loaded, total) {
                const p = total > 0 ? loaded / total : 0;
                shownProgress = Math.max(shownProgress, p);
                progressFill.style.transform = `scaleX(${shownProgress * 0.9})`;
                loadingText.textContent = `Downloading .NET runtime and game… ${loaded} / ${total}`;
            }
        })
        .withApplicationArguments()
        .create();

    loadingText.textContent = 'Starting the World…';
    progressFill.style.transform = 'scaleX(0.95)';

    // --------------------------------------------------------------- interop in
    const hud = new Float64Array(16);
    let scratch = new Float32Array(0);
    let spriteCount = 0;

    runtime.setModuleImports('main.js', {
        // Called synchronously from C# inside Tick; the views die when it returns.
        render(view, count, camX, camY, hudView) {
            spriteCount = count;
            const n = count * STRIDE;
            let floats;
            const u8 = typeof view._unsafe_create_view === 'function' ? view._unsafe_create_view() : null;
            if (u8 && (u8.byteOffset & 3) === 0) {
                floats = new Float32Array(u8.buffer, u8.byteOffset, n);   // zero-copy over wasm memory
            } else {
                if (scratch.length < n) scratch = new Float32Array(Math.max(n, scratch.length * 2));
                view.copyTo(new Uint8Array(scratch.buffer, 0, n * 4));
                floats = scratch;
            }
            renderer.upload(floats, count, camX, camY);
            hudView.copyTo(hud);
        }
    });

    const exports = await runtime.getAssemblyExports('KenseiECS.Demo.Web');
    const G = exports.KenseiECS.Demo.Web.Interop;
    const seed = Number(params.get('seed')) || ((Math.random() * 0x7fffffff) | 0) || 1;
    G.Init(seed);

    // --------------------------------------------------------------- state
    const readHud = () => ({
        hp: hud[HUD_HP], maxHp: hud[HUD_MAXHP], level: hud[HUD_LEVEL], xp: hud[HUD_XP],
        xpToNext: hud[HUD_XPNEXT], time: hud[HUD_TIME], kills: hud[HUD_KILLS], status: hud[HUD_STATUS],
        choice0: hud[HUD_C0], choice1: hud[HUD_C1], choice2: hud[HUD_C2], enemies: hud[HUD_ENEMIES]
    });

    let started = !embed;
    let paused = false;
    let shownStatus = -1;
    let fps = 60;
    let visualTime = 0;

    const input = new Input(canvas, $('stick'), $('stick-knob'));

    const ui = new UI({
        choose(slot) {
            if (hud[HUD_STATUS] !== LEVELUP || !ui.levelUpReady) return;
            G.ChooseUpgrade(slot);
            ui.hideLevelUp();
            syncStatus();
        },
        restart() {
            G.Restart();
            ui.hideGameOver();
            ui.hideLevelUp();
            input.clear();
            shownStatus = -1;
            syncStatus();
        },
        resume() { setPaused(false); },
        start() { startGame(); },
        togglePause() { setPaused(!paused); },
        upgradeName: id => G.UpgradeName(id),
        upgradeDescription: id => G.UpgradeDescription(id)
    });

    const overlay = new Overlay({
        stats: () => G.StatsJson(),
        setActive: (name, on) => G.SetSystemActive(name, on),
        setStress: v => G.SetStress(v),
        getStress: () => G.GetStress(),
        setGod: on => G.SetGodMode(on),
        fps: () => fps,
        sprites: () => spriteCount
    });

    function setPaused(on) {
        if (!started || hud[HUD_STATUS] === GAMEOVER) on = false;
        paused = on;
        ui.showPaused(on);
        if (on) input.clear();
    }

    function startGame() {
        started = true;
        ui.showStart(false);
        ui.showHud(true);
        canvas.focus?.();
    }

    function syncStatus() {
        const h = readHud();
        ui.update(h);
        const status = h.status;
        if (status === LEVELUP) ui.showLevelUp(h);
        if (status === shownStatus) return;
        if (status !== LEVELUP) ui.hideLevelUp();
        if (status === GAMEOVER) ui.showGameOver(h, hud[HUD_PEAK]);
        else ui.hideGameOver();
        shownStatus = status;
    }

    // --------------------------------------------------------------- input
    window.addEventListener('keydown', e => {
        if (e.target instanceof HTMLInputElement && e.target.type === 'range') return;
        if (e.repeat && e.code !== 'Backquote') return;
        const status = hud[HUD_STATUS];
        switch (e.code) {
            case 'Backquote':
                if (!e.repeat) overlay.toggle();
                e.preventDefault();
                break;
            case 'Digit1': case 'Digit2': case 'Digit3':
            case 'Numpad1': case 'Numpad2': case 'Numpad3':
                if (status === LEVELUP && !paused) ui.api.choose(+e.code.slice(-1) - 1);
                break;
            case 'KeyP': case 'Escape':
                if (started && status !== GAMEOVER) setPaused(!paused);
                break;
            case 'KeyR': case 'Enter': case 'NumpadEnter':
                if (status === GAMEOVER) { ui.api.restart(); e.preventDefault(); }
                else if (!started && e.code !== 'KeyR') { startGame(); e.preventDefault(); }
                break;
            case 'Space':
                if (!started) { startGame(); e.preventDefault(); }
                break;
        }
    });

    document.addEventListener('visibilitychange', () => {
        if (document.hidden && started && hud[HUD_STATUS] === PLAYING) setPaused(true);
    });

    // --------------------------------------------------------------- loop
    let last = performance.now();
    function frame(now) {
        requestAnimationFrame(frame);
        const elapsed = Math.max(0, (now - last) / 1000);
        last = now;
        if (elapsed > 0) fps += (Math.min(1 / elapsed, 240) - fps) * 0.05;

        // Hidden tabs get no rAF at all; visibilitychange above also pauses the game.
        if (started && !paused) {
            const dt = Math.min(elapsed, MAX_DT);
            const [ix, iy] = input.read();
            G.Tick(dt, ix, iy);       // -> render() above
            visualTime += dt;
            syncStatus();
        }
        renderer.draw(visualTime);
    }

    // --------------------------------------------------------------- go
    syncStatus();
    $('loading').classList.add('done');
    setTimeout(() => $('loading').remove(), 400);
    const wide = window.matchMedia('(min-width: 900px)').matches;
    if (!embed && wide && params.get('overlay') !== '0') overlay.toggle(true);
    if (embed) ui.showStart(true); else ui.showHud(true);
    requestAnimationFrame(frame);

    // Handy for poking from the console: horde.G.SetStress(25)
    window.horde = { G, hud, input, overlay, ui, renderer, get fps() { return fps; } };
}

main().catch(err => {
    console.error(err);
    fail('The demo failed to start.', err && (err.stack || err.message || err));
});
