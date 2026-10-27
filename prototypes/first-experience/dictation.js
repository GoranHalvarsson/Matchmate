'use strict';
(() => {
  const mic = document.querySelector('#dictate');
  const input = document.querySelector('#question');
  const form = document.querySelector('#composer');
  const send = form.querySelector('[type="submit"]');
  const status = document.querySelector('#dictation-status');
  const Recognition = window.SpeechRecognition || window.webkitSpeechRecognition;
  let active = null;

  function controls(listening) {
    mic.setAttribute('aria-pressed', String(listening));
    mic.setAttribute('aria-label', listening ? 'Stop dictation' : 'Dictate message');
    mic.title = listening ? 'Stop dictation' : 'Dictate message';
    input.readOnly = listening;
    send.disabled = listening;
  }

  // Discard late events when leaving the speaker or page. Keep received text.
  function cancel() {
    if (!active) return;
    const session = active;
    active = null;
    session.abort();
    controls(false);
    status.textContent = 'Dictation stopped. Review your text before sending.';
  }

  mic.addEventListener('click', () => {
    if (active) { active.stop(); status.textContent = 'Finishing dictation…'; return; }
    if (!Recognition) {
      status.textContent = 'Dictation isn’t available in this browser. Try Chrome, or use your keyboard’s dictation.';
      input.focus();
      return;
    }
    const original = input.value;
    const session = new Recognition();
    session.lang = document.documentElement.lang || 'en';
    session.continuous = false;
    session.interimResults = true;
    let received = false;
    let failed = false;
    active = session;
    controls(true);
    status.textContent = 'Starting microphone… Allow access if your browser asks.';
    session.onstart = () => {
      if (active === session) status.textContent = 'Listening in English… Tap the microphone to stop.';
    };
    session.onresult = event => {
      if (active !== session) return;
      // Results contain the full current utterance: replace it, never append twice.
      const speech = Array.from(event.results, result => result[0].transcript).join(' ').trim();
      received = received || Boolean(speech);
      input.value = original + (original && speech && !/\s$/.test(original) ? ' ' : '') + speech;
    };
    session.onerror = event => {
      if (active !== session) return;
      failed = true;
      status.textContent = ({
        'not-allowed': 'Microphone access was denied. You can enable it in browser settings or keep typing.',
        'service-not-allowed': 'The browser’s speech service is unavailable. You can keep typing.',
        'audio-capture': 'No microphone was found. Check your microphone or keep typing.',
        'no-speech': 'No speech was detected. Tap the microphone to try again.',
        'network': 'The speech service couldn’t connect. Check your connection or keep typing.',
        'aborted': 'Dictation stopped. Your existing text is kept.'
      })[event.error] || 'Dictation couldn’t start. You can keep typing or try again.';
      active = null;
      session.abort();
      controls(false);
    };
    session.onend = () => {
      if (active !== session) return;
      active = null;
      controls(false);
      if (!failed) status.textContent = received ? 'Ready to review. Edit your message, then press Send.' : 'No speech was detected. Tap the microphone to try again.';
      input.focus();
    };
    try { session.start(); } catch {
      active = null;
      controls(false);
      status.textContent = 'Dictation couldn’t start in this browser. You can keep typing.';
    }
  });
  form.addEventListener('submit', event => {
    if (active) { event.preventDefault(); event.stopImmediatePropagation(); }
  }, true);
  document.addEventListener('click', event => {
    if (active && !form.contains(event.target)) cancel();
  }, true);
  document.addEventListener('visibilitychange', () => { if (document.hidden) cancel(); });
  window.addEventListener('pagehide', cancel);
})();
