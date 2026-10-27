import { saveLanguage } from './Components/Experience/ReplayLanguageSelector.razor.js';

const CULTURES = new Map([['English', 'en-GB'], ['Swedish', 'sv-SE'], ['Spanish', 'es-ES']]);
const boundSelectors = new WeakSet();

function bindStandaloneLanguages() {
    for (const container of document.querySelectorAll('[data-standalone-language]')) {
        const select = container.querySelector('select');
        if (!select) continue;
        if (!boundSelectors.has(select)) {
            select.addEventListener('change', () => {
                const currentContainer = select.closest('[data-standalone-language]');
                if (!currentContainer || select.disabled) return;
                const error = currentContainer.querySelector('[data-language-error]');
                const previousValue = select.querySelector('option[selected]')?.value;
                const option = select.selectedOptions[0];
                const culture = CULTURES.get(option?.value);
                select.disabled = true;
                currentContainer.setAttribute('aria-busy', 'true');
                if (error) { error.textContent = ''; error.hidden = true; }
                try {
                    if (!culture || option.dataset.culture !== culture || !saveLanguage(culture).saved) throw new Error('Language was not saved.');
                    // Reload the current URL, preserving its query (including returnUrl) and fragment.
                    location.reload();
                } catch {
                    if (previousValue) select.value = previousValue;
                    if (error) { error.textContent = currentContainer.dataset.languageSaveFailed ?? ''; error.hidden = false; }
                } finally {
                    currentContainer.setAttribute('aria-busy', 'false');
                    select.disabled = false;
                }
            });
            boundSelectors.add(select);
        }
        // Static SSR starts disabled; only enable after the public handler is installed.
        select.disabled = false;
    }
}

export function afterWebStarted(blazor) {
    bindStandaloneLanguages();
    blazor.addEventListener('enhancedload', bindStandaloneLanguages);
}
