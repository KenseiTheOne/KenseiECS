// HUD, level-up cards, pause / game-over screens.

const $ = id => document.getElementById(id);

export function formatTime(sec) {
    sec = Math.max(0, Math.floor(sec));
    const m = Math.floor(sec / 60), s = sec % 60;
    return `${m}:${s < 10 ? '0' : ''}${s}`;
}

const fmt = new Intl.NumberFormat('en-US');

/** Writes text only when it changed: the HUD updates every frame. */
function setter(el) {
    let last;
    return v => { if (v !== last) { last = v; el.textContent = v; } };
}

function widthSetter(el) {
    let last = -1;
    return frac => {
        const v = Math.round(Math.max(0, Math.min(1, frac)) * 1000);
        if (v !== last) { last = v; el.style.transform = `scaleX(${v / 1000})`; }
    };
}

export class UI {
    /**
     * @param {object} api  { choose(slot), restart(), upgradeName(id), upgradeDescription(id) }
     */
    constructor(api) {
        this.api = api;
        this.hud = $('hud');
        this.setLevel = setter($('hud-level'));
        this.setHpText = setter($('hp-text'));
        this.setHp = widthSetter($('hp-fill'));
        this.setXp = widthSetter($('xp-fill'));
        this.setTime = setter($('hud-time'));
        this.setKills = setter($('hud-kills'));
        this.setEnemies = setter($('hud-enemies'));
        this.hpBar = $('hp-fill').parentElement;

        this.levelup = $('levelup');
        this.cards = $('cards');
        this.paused = $('paused');
        this.gameover = $('gameover');
        this.start = $('start');
        this.choiceKey = '';
        this.lastHp = -1;

        $('btn-restart').addEventListener('click', () => api.restart());
        $('btn-resume').addEventListener('click', () => api.resume());
        // Anywhere on the start screen starts the game (the button click bubbles here too).
        this.start.addEventListener('click', () => api.start());
        if (window.matchMedia?.('(hover: none) and (pointer: coarse)').matches) $('btn-start').textContent = 'Tap to play';
        $('btn-pause').addEventListener('click', () => api.togglePause());
    }

    showHud(on) { this.hud.classList.toggle('hidden', !on); }

    /** Per-frame HUD refresh. h = HUD values (see HUD_* in main.js). */
    update(h) {
        this.setLevel(`Lv ${h.level}`);
        this.setHp(h.maxHp > 0 ? h.hp / h.maxHp : 0);
        this.setHpText(`${Math.ceil(Math.max(0, h.hp))} / ${Math.round(h.maxHp)}`);
        this.setXp(h.xpToNext > 0 ? h.xp / h.xpToNext : 0);
        this.setTime(formatTime(h.time));
        this.setKills(fmt.format(h.kills));
        this.setEnemies(fmt.format(h.enemies));
        if (h.hp < this.lastHp) {
            this.hpBar.classList.remove('hit');
            void this.hpBar.offsetWidth;   // restart the CSS animation
            this.hpBar.classList.add('hit');
        }
        this.hpBar.classList.toggle('low', h.hp / h.maxHp < 0.3);
        this.lastHp = h.hp;
    }

    showLevelUp(h) {
        const ids = [h.choice0, h.choice1, h.choice2];
        const key = `${h.level}:${ids.join(',')}`;
        if (!this.levelup.classList.contains('hidden') && key === this.choiceKey) return;
        this.choiceKey = key;
        this.cards.textContent = '';
        ids.forEach((id, slot) => {
            const card = document.createElement('button');
            card.className = 'card';
            card.type = 'button';
            card.innerHTML = `<span class="card-key">${slot + 1}</span><span class="card-name"></span><span class="card-desc"></span>`;
            card.querySelector('.card-name').textContent = this.api.upgradeName(id);
            card.querySelector('.card-desc').textContent = this.api.upgradeDescription(id);
            card.addEventListener('click', () => this.api.choose(slot));
            this.cards.appendChild(card);
        });
        this.levelup.classList.remove('hidden');
        // Ignore clicks for a moment so a click meant for the game doesn't pick a card.
        this.cards.classList.add('cooldown');
        clearTimeout(this._cool);
        this._cool = setTimeout(() => this.cards.classList.remove('cooldown'), 350);
    }

    hideLevelUp() {
        this.levelup.classList.add('hidden');
        this.choiceKey = '';
    }

    get levelUpReady() { return !this.cards.classList.contains('cooldown'); }

    showPaused(on) { this.paused.classList.toggle('hidden', !on); }

    showGameOver(h, peak) {
        $('go-time').textContent = formatTime(h.time);
        $('go-kills').textContent = fmt.format(h.kills);
        $('go-level').textContent = String(h.level);
        $('go-peak').textContent = fmt.format(peak);
        this.gameover.classList.remove('hidden');
    }

    hideGameOver() { this.gameover.classList.add('hidden'); }

    showStart(on) { this.start.classList.toggle('hidden', !on); }
}
