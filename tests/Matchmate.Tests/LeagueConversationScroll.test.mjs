import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../../src/Matchmate.Web/Components/Experience/LeagueConversation.razor.js', import.meta.url), 'utf8');
const { updateThread } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

class Thread extends EventTarget {
    clientHeight = 354;
    clientTop = 1;
    scrollHeight = 1949;
    scrollTop = 0;
    turns = new Map();
    getBoundingClientRect() { return { top: 200 }; }
    querySelectorAll() {
        return [...this.turns].map(([id, top]) => ({
            dataset: { turnId: id },
            getBoundingClientRect: () => ({ top: 201 + top - this.scrollTop }),
            focus: () => assert.fail('Revealing a reply must not focus it')
        }));
    }
    readAt(top) { this.dispatchEvent(new Event('wheel')); this.scrollTop = top; this.dispatchEvent(new Event('scroll')); }
}

test('reveals the start of the appended answer inside the thread only', () => {
    const thread = new Thread(); thread.turns.set('first', 0); thread.turns.set('second', 1100);
    updateThread(thread, 1, true, null);
    updateThread(thread, 1, false, 'second');
    assert.equal(thread.scrollTop, 1100);
    updateThread(thread, 1, false, null);
    assert.equal(thread.scrollTop, 1100);
});

test('keeps the reader position when they scroll while waiting, including back to the starting position', () => {
    const thread = new Thread(); thread.turns.set('latest', 1600); thread.scrollTop = 500;
    updateThread(thread, 2, true, null);
    thread.readAt(120); thread.readAt(500);
    updateThread(thread, 2, false, 'latest');
    assert.equal(thread.scrollTop, 500);
});

test('a fresh submission can reveal after an earlier request preserved reader position', () => {
    const thread = new Thread(); thread.turns.set('second', 1100); thread.turns.set('third', 1700);
    updateThread(thread, 1, true, null); thread.readAt(100);
    updateThread(thread, 1, false, 'second'); assert.equal(thread.scrollTop, 100);
    updateThread(thread, 2, true, null);
    updateThread(thread, 2, false, 'third'); assert.equal(thread.scrollTop, 1700);
});

test('completion while collapsed retains the reader choice for a queued reveal on reopen', () => {
    for (const moved of [false, true]) {
        const thread = new Thread(); thread.turns.set('latest', 1600); thread.scrollTop = 500;
        updateThread(thread, 1, true, null);
        if (moved) thread.readAt(100);
        thread.clientHeight = 0; updateThread(thread, 1, false, null);
        thread.clientHeight = 354; updateThread(thread, 1, false, 'latest');
        assert.equal(thread.scrollTop, moved ? 100 : 1600);
    }
});

test('unrelated renders, context changes and hidden or missing targets do not move the viewport', () => {
    const thread = new Thread(); thread.turns.set('second', 1100); thread.scrollTop = 280;
    updateThread(thread, 1, false, null); updateThread(thread, 2, false, null);
    assert.equal(thread.scrollTop, 280);
    thread.clientHeight = 0; updateThread(thread, 3, false, 'second'); assert.equal(thread.scrollTop, 280);
    thread.clientHeight = 354; updateThread(thread, 3, false, null); assert.equal(thread.scrollTop, 280);
    updateThread(thread, 4, false, 'missing'); assert.equal(thread.scrollTop, 280);
});

test('independent threads do not share pending-reader state', () => {
    const first = new Thread(); const second = new Thread();
    first.turns.set('latest', 1000); second.turns.set('latest', 1000);
    updateThread(first, 1, true, null); updateThread(second, 1, true, null); first.readAt(120);
    updateThread(first, 1, false, 'latest'); updateThread(second, 1, false, 'latest');
    assert.equal(first.scrollTop, 120); assert.equal(second.scrollTop, 1000);
});

test('browser scroll anchoring without reader input does not suppress a newly accepted reply', () => {
    const thread = new Thread(); thread.turns.set('latest', 1600); thread.scrollTop = 500;
    updateThread(thread, 1, true, null);
    thread.scrollTop = 300; thread.dispatchEvent(new Event('scroll'));
    updateThread(thread, 1, false, 'latest'); assert.equal(thread.scrollTop, 1600);
});
