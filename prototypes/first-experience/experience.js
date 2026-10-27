'use strict';

// The original scenario is shared by both layouts. All continuity is visit-only.
const previousRender = render;
const previousAnswer = answer;
const previousOpening = opening;
const topics = new Map();
let route = [];
let hasAnswered = false;
const voices = {
  guide: { label: 'YOUR GUIDE', detail: 'A good place to start. A familiar voice to return to.' },
  match: { label: 'MATCH VOICE', detail: 'The moments. The questions. The game so far.' },
  harbour: { label: 'CLUB VOICE', detail: 'Port-town roots. Front-foot football.' },
  riverside: { label: 'CLUB VOICE', detail: 'A little patience. An eye for the opening.' },
  lantern: { label: 'STADIUM VOICE', detail: 'Harbour’s home. Close to the touchline since 1928.' },
  park: { label: 'STADIUM VOICE', detail: 'Riverside’s home, beside the river.' }
};

function currentTopic() { return topics.get(key()); }
function scrollBehavior() { return matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth'; }
function destinations() {
  return speaker === 'match' ? ['harbour', 'riverside', selected === 'upcoming' ? 'park' : 'lantern']
    : speaker === 'harbour' ? ['lantern', 'match']
    : speaker === 'riverside' ? ['park', 'match']
    : ['match', speaker === 'lantern' ? 'harbour' : 'riverside'];
}

opening = function () {
  if (speaker !== 'guide') return previousOpening();
  return prefs.club === 'neutral'
    ? 'Let’s start with a game in the balance. Harbour and Riverside are level at 1–1, with 65 minutes played.\n\nI can take you through the goals, then introduce you to a club or its ground.'
    : `You’re following ${names[prefs.club]}, with ${prefs.depth === 'tactical' ? 'room for tactical detail' : 'clear, simple explanations'}.\n\nLet’s start with the level game, or choose a past or upcoming meeting. I’ll keep your place as you explore.`;
};

handoffs = function () {
  if (speaker === 'guide') return '';
  const topic = currentTopic();
  return `<div class="next-voices"><div class="eyebrow">${topic ? 'TAKE THIS QUESTION FURTHER' : 'ANOTHER SIDE OF THE STORY'}</div>${topic ? `<p class="topic-preview">“${escapeHTML(topic)}”</p>` : ''}<div class="handoffs">${destinations().map(id => `<button data-${topic ? 'carry' : 'entity'}="${id}">${topic ? 'Ask' : 'Meet'} ${id === 'match' ? 'the match' : names[id]} <span aria-hidden="true">↗</span></button>`).join('')}</div></div>`;
};

function fixtureSummary(f) {
  return f.id === 'live' ? '65′ · 1–1 · Replay paused' : f.id === 'past' ? 'Full time · 2–1 · 04 Oct' : '18 Oct · 19:45 · Not started';
}

render = function () {
  previousRender();
  const f = fixtures[selected];
  $('.conversation').dataset.voice = speaker;
  $('#speaker-subtitle').textContent = voices[speaker].label + (speaker === 'guide' ? ' · THIS VISIT' : ' · FICTIONAL PERSONA');
  $('#speaker-name').setAttribute('tabindex', '-1');
  $('#context').innerHTML = speaker === 'guide'
    ? `<div class="guide-scene"><div class="eyebrow">A GOOD GAME TO START WITH</div><h2>Level at 65′.<br>How did we get here?</h2><div class="pitch-mark" aria-hidden="true"><i></i></div><p>Harbour FC · Riverside United</p><span class="replay-label">DEMO REPLAY · PAUSED · 1–1</span></div>`
    : MatchWorkspace.context({stateClass:selected,state:f.label==='Live'?'LIVE REPLAY':f.label.toUpperCase(),title:f.title,subtitle:fixtureSummary(f)+' · '+f.venue,sources:'<button class="text-button" id="sources">Sources ↗</button>'})+`<div class="voice-detail">${voices[speaker].detail}</div>`;
  $('#journey').innerHTML = MatchWorkspace.journey(route.length?`<button id="back-voice">← ${escapeHTML(names[route[route.length-1].speaker])}</button>`:'<span>Your football, in conversation</span>','<button id="browse-football">Explore matches, clubs & grounds ↓</button>');
  const nudge = hasAnswered && prefs.club === 'neutral' && speaker !== 'guide'
    ? '<button class="preference-nudge" id="personalize-later">Enjoying this? Choose your club & conversation style →</button>' : '';
  $('#suggestions').insertAdjacentHTML('afterend', nudge);
  // render() may be called repeatedly; keep a single optional invitation.
  document.querySelectorAll('.preference-nudge').forEach((el, index) => { if (index > 0) el.remove(); });
  if (!nudge) document.querySelectorAll('.preference-nudge').forEach(el => el.remove());
  if (speaker === 'guide') {
    $('#suggestions').innerHTML = '<button class="start-conversation" data-prompt="Catch me up">Catch me up on this match <span>→</span></button><div class="other-starts"><button data-prompt="Take me back">Revisit the comeback</button><button data-prompt="What’s coming next?">Look ahead</button></div>';
    const last = route[route.length - 1];
    const lastQuestion = last && topics.get(`${last.speaker}:${last.selected}`);
    if (lastQuestion) {
      $('#thread').insertAdjacentHTML('beforeend', `<div class="guide-recall"><div class="eyebrow">WHERE YOU LEFT OFF · ${fixtures[last.selected].label.toUpperCase()}</div><p>You asked ${escapeHTML(names[last.speaker])}: “${escapeHTML(lastQuestion)}”</p><button id="resume-topic">Continue with ${escapeHTML(names[last.speaker])} →</button></div>`);
    }
  }
  $('#thread').querySelectorAll('.message').forEach((el, index) => {
    const m = threads[key()][index];
    if (m && m.carried) el.insertAdjacentHTML('afterbegin', `<div class="handoff-divider">QUESTION BROUGHT FROM ${escapeHTML(names[m.from].toUpperCase())}</div>`);
    if (m && m.note) el.insertAdjacentHTML('beforeend', `<span class="answer-kind">${escapeHTML(m.note)}</span>`);
  });
  $('#thread').scrollTop = $('#thread').scrollHeight;
};

open = function (id, fixture, options = {}) {
  const nextFixture = fixture || selected;
  if (id !== speaker || nextFixture !== selected) route.push({ speaker, selected });
  speaker = id;
  selected = nextFixture;
  if (fixture) { filter = fixture; catalog(); }
  render();
  if (!options.quiet) $('#speaker-name').focus({ preventScroll: true });
  if (innerWidth < 961) $('.conversation').scrollIntoView({ behavior: scrollBehavior(), block: 'start' });
};

answer = function (q) {
  const startCatchup = speaker === 'guide' && q === 'Catch me up';
  const supported = prompts().includes(q);
  if (speaker !== 'guide' && supported) { topics.set(key(), q); hasAnswered = true; }
  previousAnswer(q);
  if (startCatchup && !threads[key()].some(message => message.user && message.text === 'How did we get here?')) {
    topics.set(key(), 'How did we get here?');
    hasAnswered = true;
    previousAnswer('How did we get here?');
  }
};

function carriedAnswer(target, question) {
  if (target === 'match') return `Let’s bring “${question}” back to this ${fixtures[selected].label.toLowerCase()} fixture.\n\n${previousOpening()}\n\nThe event record can describe the goals; it cannot establish how the crowd or a club’s usual style caused them.`;
  if (target === 'harbour' || target === 'riverside') {
    const style = target === 'harbour' ? 'My usual approach is to press early. That can hurry an opponent, but it also leaves space to defend.' : 'My usual approach is patient passing: invite pressure, then look for the space it leaves.';
    return `You brought “${question}” from ${names[speaker]}. Here’s my side.\n\n${style}\n\n${selected === 'upcoming' ? 'That gives you something to watch for when we meet. It does not tell us who will win.' : 'That is my club identity, not evidence of what happened in this match. We would need more event detail to make that connection.'}`;
  }
  return `You brought “${question}” with you. I can add the sense of place.\n\n${target === 'lantern' ? 'At The Lantern, Harbour’s West Stand sits close to the touchline. I imagine every challenge feeling a little nearer from there.' : 'At Riverside Park, the river sits beside the ground. This is Riverside’s home and the venue for the upcoming meeting.'}\n\n${selected === 'upcoming' ? 'The match is still ahead of us; this is an introduction to the setting, not a prediction.' : 'That is atmosphere from my fictional profile, not proof that the ground changed the result.'}`;
}

document.addEventListener('click', event => {
  const button = event.target.closest('button');
  if (!button) return;
  if (button.dataset.carry) {
    const question = currentTopic();
    if (!question) return;
    const from = speaker;
    const target = button.dataset.carry;
    // Generate while source identity is still active, then enter the destination thread.
    const reply = target === 'match' ? null : carriedAnswer(target, question);
    open(target);
    topics.set(key(), question);
    threads[key()].push({ text: question, user: true, carried: true, from }, {
      text: reply || carriedAnswer(target, question), user: false,
      note: target === 'match' ? 'Fixture record · Limits stated above' : 'Club / ground perspective · Not a match fact'
    });
    render();
  }
  if ((button.id === 'back-voice' || button.id === 'resume-topic') && route.length) {
    const prior = route.pop();
    speaker = prior.speaker;
    selected = prior.selected;
    render();
    $('#speaker-name').focus({ preventScroll: true });
  }
  if (button.id === 'browse-football') {
    $('.explore').scrollIntoView({ behavior: scrollBehavior(), block: 'start' });
    $('.sections button').focus({ preventScroll: true });
  }
  if (button.id === 'personalize-later') $('#preferences').click();
  if (button.id === 'reset-prefs') { topics.clear(); route = []; hasAnswered = false; render(); }
});

delete threads.guide;
// Keep the reading and keyboard order consistent with the mobile composition.
$('.explore').before($('.conversation'));
render();
