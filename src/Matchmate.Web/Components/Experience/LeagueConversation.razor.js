const threads = new WeakMap();

export function updateThread(thread, request, pending, revealTurn) {
    if (!thread) return;
    let state = threads.get(thread);
    if (!state) {
        state = { request: null, pending: false, reading: false, moved: false, start: thread.scrollTop };
        threads.set(thread, state);
        // The listener belongs to the element; the WeakMap does not retain removed threads.
        thread.addEventListener('scroll', () => {
            if (state.pending && state.reading && Math.abs(thread.scrollTop - state.start) > 1) state.moved = true;
        }, { passive: true });
        for (const event of ['wheel', 'touchmove', 'pointerdown']) {
            thread.addEventListener(event, () => { if (state.pending) state.reading = true; }, { passive: true });
        }
        thread.addEventListener('keydown', event => {
            if (state.pending && ['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', ' '].includes(event.key)) state.reading = true;
        });
    }
    if (state.request !== request) {
        state.request = request;
        state.moved = false;
        state.reading = false;
        state.start = thread.scrollTop;
    }
    const preserve = state.moved;
    state.pending = pending;
    if (!revealTurn || preserve || thread.clientHeight === 0) return;
    const turn = [...thread.querySelectorAll('[data-turn-id]')].find(element => element.dataset.turnId === revealTurn);
    if (!turn) return;
    // Change only this viewport: scrollIntoView could also move the page and composer.
    thread.scrollTop += turn.getBoundingClientRect().top - thread.getBoundingClientRect().top - thread.clientTop;
}
