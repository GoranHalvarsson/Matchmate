const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');

function setup(supported = true) {
  const element = () => ({ value: '', attrs: {}, listeners: {},
    addEventListener(type, fn) { this.listeners[type] = fn; },
    setAttribute(name, value) { this.attrs[name] = value; }, focus() {},
  });
  const elements = Object.fromEntries(['#dictate', '#question', '#composer', '#dictation-status', 'send'].map(id => [id, element()]));
  const form = elements['#composer'];
  form.querySelector = () => elements.send;
  form.contains = target => Object.values(elements).includes(target);
  let session;
  class Recognition {
    constructor() { session = this; }
    start() { this.onstart(); }
    stop() { this.onend(); }
    abort() { this.aborted = true; }
  }
  const document = { documentElement: { lang: 'en' }, listeners: {}, querySelector: id => elements[id], addEventListener(type, fn) { this.listeners[type] = fn; } };
  vm.runInNewContext(fs.readFileSync(require('node:path').join(__dirname, 'dictation.js'), 'utf8'), {
    document, window: { SpeechRecognition: supported ? Recognition : undefined, addEventListener() {} }
  });
  return { elements, document, session: () => session, click: () => elements['#dictate'].listeners.click() };
}

test('dictation keeps the draft, replaces interim text, and never submits', () => {
  const s = setup(); const input = s.elements['#question'];
  input.value = 'Explain'; s.click();
  assert.equal(s.elements.send.disabled, true);
  s.session().onresult({ results: [[{ transcript: 'the' }]] });
  s.session().onresult({ results: [[{ transcript: 'the match' }]] });
  assert.equal(input.value, 'Explain the match');
  s.click(); assert.equal(s.elements.send.disabled, false);
  assert.equal(input.readOnly, false);
});
test('cancellation ignores late recognition results', () => {
  const s = setup(); s.elements['#question'].value = 'Keep this'; s.click();
  s.document.listeners.click({ target: {} });
  assert.equal(s.session().aborted, true);
  s.session().onresult({ results: [[{ transcript: 'late result' }]] });
  assert.equal(s.elements['#question'].value, 'Keep this');
  assert.equal(s.elements.send.disabled, false);
});
test('permission denial keeps existing text and unlocks the input', () => {
  const s = setup(); s.elements['#question'].value = 'Keep this'; s.click();
  s.session().onerror({ error: 'not-allowed' });
  assert.match(s.elements['#dictation-status'].textContent, /denied/);
  assert.equal(s.elements['#question'].value, 'Keep this');
  assert.equal(s.elements.send.disabled, false);
});
test('unsupported browser gives an actionable fallback', () => {
  const s = setup(false); s.click();
  assert.match(s.elements['#dictation-status'].textContent, /isn’t available/);
  assert.equal(s.session(), undefined);
});
