export function focusPitch(pitch) {
    if (!pitch?.isConnected) return;
    pitch.focus({ preventScroll: true });
    const reduced = globalThis.matchMedia('(prefers-reduced-motion: reduce)').matches;
    pitch.scrollIntoView({ behavior: reduced ? 'instant' : 'smooth', block: 'center' });
}
