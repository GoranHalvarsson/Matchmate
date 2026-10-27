'use strict';

// A visit-only interaction demo. This file never creates an identity or stores data.
let guestDismissed = false;
let accountPreview = false;
const renderBeforeGuest = render;
const guestBadge = document.createElement('span');
guestBadge.className = 'guest-badge';
guestBadge.setAttribute('role', 'status');
document.querySelector('header').insertBefore(guestBadge, $('#preferences'));

function usefulExchanges() {
  return Object.entries(threads).filter(([id]) => id !== 'guide').flatMap(([id, messages]) =>
    messages.flatMap((message, index) => {
      const reply = messages[index + 1];
      return message.user && reply && !reply.user && !/scripted design preview|can’t answer that freely/.test(reply.text)
        ? [{ id, question: message.text }] : [];
    }));
}

render = function () {
  renderBeforeGuest();
  const exchanges = usefulExchanges();
  guestBadge.textContent = accountPreview ? 'Account preview' : 'Guest visit';
  // Avoid competing preference and registration requests after an answer.
  document.querySelectorAll('.preference-nudge').forEach(element => element.remove());
  if (speaker === 'guide' && !hasAnswered) {
    $('#speaker-subtitle').textContent = 'YOUR GUIDE · NO ACCOUNT NEEDED';
    $('.start-conversation').insertAdjacentHTML('afterend', '<span class="guest-reassurance">Jump in. Type or dictate. No sign-up to start.</span>');
  }
  const slot = $('#guest-invitation');
  if (accountPreview) {
    slot.innerHTML = '<div class="guest-confirmation"><strong>Your conversation stays right here.</strong><span>Account preview only · No real account or lasting save. Reload resets this visit.</span></div>';
  } else if (!guestDismissed && exchanges.length >= 2) {
    slot.innerHTML = '<div class="save-invitation"><div class="eyebrow">SOMETHING WORTH KEEPING</div><strong>Keep this conversation. Pick up next time.</strong><p>An account would keep your chats and chosen preferences together.</p><div><button id="save-conversation">Save my conversation →</button><button id="keep-exploring">Keep exploring</button></div><small>Optional account preview · You can continue as a guest.</small></div>';
  } else {
    slot.innerHTML = exchanges.length ? '<div class="guest-footnote">You’re exploring as a guest. This preview resets when you reload.</div>' : '';
  }
};

function dismissInvitation() {
  guestDismissed = true;
  $('#signup-preview').close();
  render();
  $('#question').focus({ preventScroll: true });
}

document.addEventListener('click', event => {
  const button = event.target.closest('button');
  if (!button) return;
  if (button.id === 'save-conversation') {
    const exchanges = usefulExchanges();
    const currentExchanges = exchanges.filter(exchange => exchange.id === key());
    const latest = currentExchanges[currentExchanges.length - 1] || exchanges[exchanges.length - 1];
    const [voice, fixture] = latest.id.split(':');
    $('#save-summary').innerHTML = `<div class="save-summary"><strong>Your visit so far</strong><p>${exchanges.length} exchanges across ${new Set(exchanges.map(item => item.id)).size} conversations</p><p>A topic to keep: “${escapeHTML(latest.question)}”<br>${escapeHTML(names[voice])} · ${fixtures[fixture].label}</p><p>${prefs.club === 'neutral' ? 'No favourite club chosen' : 'Following ' + names[prefs.club]} · ${prefs.depth === 'tactical' ? 'Tactical detail' : 'Simple explanations'}</p></div>`;
    $('#signup-preview').showModal();
  }
  if (button.id === 'keep-exploring' || button.id === 'signup-keep-exploring') dismissInvitation();
  if (button.id === 'simulate-signup') {
    accountPreview = true;
    $('#signup-preview').close();
    render();
    $('#question').focus({ preventScroll: true });
  }
  if (button.id === 'reset-prefs') {
    guestDismissed = false;
    accountPreview = false;
    render();
  }
});
// Closing the invitation is a choice to keep exploring, not a reason to ask again.
$('#signup-preview').addEventListener('close', () => {
  if (!accountPreview) { guestDismissed = true; render(); }
});
render();
