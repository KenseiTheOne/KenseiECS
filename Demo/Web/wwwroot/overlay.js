// ECS inspector: live view of the World and the SystemsRunner, refreshed 4x/sec
// from HordeGame.StatsJson(). Toggling a checkbox calls SetSystemActive.

const $ = id => document.getElementById(id);
const REPO = 'https://github.com/KenseiTheOne/KenseiECS/blob/main/';
const STRESS_MIN = 0.25, STRESS_MAX = 25;
const fmt = new Intl.NumberFormat('en-US');

export const stressToSlider = s => Math.round(1000 * Math.log(s / STRESS_MIN) / Math.log(STRESS_MAX / STRESS_MIN));
export const sliderToStress = v => STRESS_MIN * Math.pow(STRESS_MAX / STRESS_MIN, v / 1000);

function fmtBytes(b) {
    if (b < 1024) return `${b} B`;
    if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KB`;
    return `${(b / 1048576).toFixed(2)} MB`;
}

function fmtMs(ms) {
    if (ms >= 10) return ms.toFixed(1);
    if (ms >= 1) return ms.toFixed(2);
    return ms.toFixed(3);
}

export class Overlay {
    /**
     * @param {object} api { stats(): string, setActive(name, on): bool, setStress(v), getStress(), setGod(on),
     *                       fps(): number, sprites(): number }
     */
    constructor(api) {
        this.api = api;
        this.el = $('overlay');
        this.systemsEl = $('ov-systems');
        this.filtersEl = $('ov-filters');
        this.poolsEl = $('ov-pools');
        this.rows = new Map();
        this.phaseRows = new Map();
        this.open = false;
        this.timer = 0;

        $('overlay-close').addEventListener('click', () => this.toggle(false));
        $('btn-overlay').addEventListener('click', () => this.toggle());

        const slider = $('stress');
        slider.value = String(stressToSlider(api.getStress()));
        const applyStress = () => {
            const v = sliderToStress(+slider.value);
            // snap near 1x so "normal" is easy to get back to
            const s = Math.abs(Math.log(v)) < 0.06 ? 1 : v;
            api.setStress(s);
            this.showStress(s);
        };
        slider.addEventListener('input', applyStress);
        this.showStress(api.getStress());
        $('btn-stress-max').addEventListener('click', () => { slider.value = '1000'; applyStress(); });
        $('btn-stress-reset').addEventListener('click', () => { slider.value = String(stressToSlider(1)); applyStress(); });
        $('god').addEventListener('change', e => api.setGod(e.target.checked));

        // Keep keyboard shortcuts working after clicking a control in the panel.
        this.el.addEventListener('change', () => document.activeElement?.blur?.());
    }

    showStress(s) {
        $('stress-val').textContent = `${s >= 10 ? s.toFixed(0) : s >= 1 ? s.toFixed(1) : s.toFixed(2)}×`;
        $('stress-val').classList.toggle('hot', s > 4);
    }

    toggle(on = !this.open) {
        this.open = on;
        this.el.classList.toggle('hidden', !on);
        $('btn-overlay').classList.toggle('active', on);
        if (on) this.refresh();
        clearInterval(this.timer);
        if (on) this.timer = setInterval(() => this.refresh(), 250);
    }

    refresh() {
        let s;
        try {
            s = JSON.parse(this.api.stats());
        } catch (e) {
            console.warn('StatsJson failed', e);
            return;
        }
        $('ov-fps').textContent = this.api.fps().toFixed(0);
        $('ov-sim').textContent = fmtMs(s.frameMs);
        $('ov-entities').textContent = fmt.format(s.entities);
        $('ov-sprites').textContent = fmt.format(this.api.sprites());
        $('timings-note').textContent = s.timings ? 'last ms · peak tick' : '(timings need KENSEI_DEBUG)';
        this.renderSystems(s.systems);
        this.renderTable(this.filtersEl, s.filters, f => [f.name || '(all)', fmt.format(f.count)]);
        const pools = s.pools.slice().sort((a, b) => b.bytes - a.bytes);
        this.renderTable(this.poolsEl, pools, p => [p.type, fmt.format(p.count), fmtBytes(p.bytes)]);
    }

    renderSystems(systems) {
        let scale = 0.25;
        for (const sys of systems) scale = Math.max(scale, sys.lastMs * 1.15);
        let currentPhase = null;
        for (const sys of systems) {
            if (sys.phase !== currentPhase) {
                currentPhase = sys.phase;
                this.ensurePhaseRow(sys.phase);
            }
            let row = this.rows.get(sys.name);
            if (!row) row = this.createRow(sys);
            const phaseOn = this.phaseRows.get(sys.phase).check.checked;
            row.check.checked = sys.active;
            row.check.disabled = !phaseOn;
            row.el.classList.toggle('off', !sys.active);
            row.last.textContent = fmtMs(sys.lastMs);
            row.bar.style.transform = `scaleX(${Math.min(1, sys.lastMs / scale)})`;
            row.peak.style.left = `${Math.min(100, sys.peakMs / scale * 100)}%`;
            row.peak.style.opacity = sys.peakMs > scale ? '0' : '1';
            row.el.title = `peak ${fmtMs(sys.peakMs)} ms`;
        }
    }

    ensurePhaseRow(phase) {
        if (this.phaseRows.has(phase)) return;
        const el = document.createElement('label');
        el.className = 'ov-phase';
        el.innerHTML = `<input type="checkbox" checked><span>${phase === 'sim' ? 'Simulation phase' : 'Render phase'}</span><code>"${phase}"</code>`;
        const check = el.querySelector('input');
        check.addEventListener('change', () => {
            this.api.setActive(phase, check.checked);
            this.refresh();
        });
        this.systemsEl.appendChild(el);
        this.phaseRows.set(phase, { el, check });
    }

    createRow(sys) {
        const el = document.createElement('div');
        el.className = 'ov-sys';
        el.innerHTML = `
            <input type="checkbox" aria-label="Enable ${sys.name}">
            <a target="_blank" rel="noopener"></a>
            <span class="ov-bar"><span class="fill"></span><span class="peak"></span></span>
            <span class="ms"></span>`;
        const check = el.querySelector('input');
        const link = el.querySelector('a');
        link.textContent = sys.name;
        link.href = REPO + sys.file;
        link.title = sys.file;
        check.addEventListener('change', () => {
            this.api.setActive(sys.name, check.checked);
            this.refresh();
        });
        this.systemsEl.appendChild(el);
        const row = { el, check, bar: el.querySelector('.fill'), peak: el.querySelector('.peak'), last: el.querySelector('.ms') };
        this.rows.set(sys.name, row);
        return row;
    }

    renderTable(container, items, cells) {
        // Rebuild only if the shape changed; otherwise patch text.
        const rows = container.children;
        while (rows.length > items.length) container.lastChild.remove();
        items.forEach((item, i) => {
            const values = cells(item);
            let row = rows[i];
            if (!row || row.children.length !== values.length) {
                const fresh = document.createElement('div');
                fresh.className = 'ov-tr';
                for (let k = 0; k < values.length; k++) fresh.appendChild(document.createElement('span'));
                if (row) row.replaceWith(fresh); else container.appendChild(fresh);
                row = fresh;
            }
            values.forEach((v, k) => {
                if (row.children[k].textContent !== v) row.children[k].textContent = v;
            });
        });
    }
}
