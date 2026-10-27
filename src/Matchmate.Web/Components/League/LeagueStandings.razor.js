const tables = new WeakMap();
const duration = 300;

// Blazor alone orders and updates rows. This observer only animates their visual offsets.
export function initialize(body) {
    if (!body?.isConnected) return;
    dispose(body);
    const motion = matchMedia('(prefers-reduced-motion: reduce)');
    const animations = new Map();
    let positions = new Map(), frame = null, stopped = false;
    const rows = () => Array.from(body.rows).slice(0, 20);
    const offset = row => {
        if (!animations.has(row)) return 0;
        const transform = getComputedStyle(row).transform;
        return transform === 'none' ? 0 : new DOMMatrixReadOnly(transform).m42;
    };
    const cancel = () => {
        for (const animation of animations.values()) { animation.onfinish = null; animation.cancel(); }
        animations.clear();
    };
    const measure = () => {
        const top = body.getBoundingClientRect().top;
        return new Map(rows().map(row => [row.dataset.clubId, row.getBoundingClientRect().top - top - offset(row)]));
    };
    const baseline = () => { cancel(); positions = measure(); };
    const update = () => {
        frame = null;
        if (stopped) return;
        if (!body.isConnected) { cleanup(); return; }
        const currentRows = rows();
        const previous = new Map(currentRows.map(row => [row.dataset.clubId, (positions.get(row.dataset.clubId) ?? 0) + offset(row)]));
        cancel();
        const next = measure();
        if (!motion.matches && !document.hidden && body.rows.length <= 20) {
            for (const row of currentRows) {
                if (!positions.has(row.dataset.clubId)) continue;
                const delta = previous.get(row.dataset.clubId) - next.get(row.dataset.clubId);
                if (!Number.isFinite(delta) || Math.abs(delta) < .5 || Math.abs(delta) > 2000 || typeof row.animate !== 'function') continue;
                const animation = row.animate([{ transform: `translateY(${delta}px)` }, { transform: 'translateY(0)' }],
                    { duration, easing: 'cubic-bezier(.2,.65,.3,1)', fill: 'both' });
                animations.set(row, animation);
                animation.onfinish = () => { if (animations.get(row) === animation) { animations.delete(row); animation.cancel(); } };
            }
        }
        positions = next;
    };
    const observer = new MutationObserver(() => {
        if (!stopped && frame === null) frame = requestAnimationFrame(update);
    });
    const detached = new MutationObserver(() => { if (!body.isConnected) cleanup(); });
    const resized = new ResizeObserver(() => { if (!stopped) baseline(); });
    const reduce = () => { if (motion.matches) baseline(); };
    function cleanup() {
        if (stopped) return;
        stopped = true;
        if (frame !== null) cancelAnimationFrame(frame);
        observer.disconnect(); detached.disconnect(); resized.disconnect();
        motion.removeEventListener('change', reduce); cancel(); positions.clear(); tables.delete(body);
    }
    positions = measure();
    observer.observe(body, { childList: true, subtree: true, characterData: true });
    detached.observe(document.body, { childList: true, subtree: true });
    resized.observe(body);
    motion.addEventListener('change', reduce);
    tables.set(body, { cleanup });
}

export function dispose(body) { tables.get(body)?.cleanup(); }
