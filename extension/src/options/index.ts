/**
 * Settings page.
 *
 * Settings live in the Agent, not in chrome.storage, because the Agent is what acts on them and a
 * second copy in the browser would be a second source of truth to keep in sync. That does mean the
 * page is useless without a running Agent, which is why it says so plainly rather than accepting
 * changes it cannot save.
 */

interface AgentState {
  type?: string;
  agentVersion?: string;
  enabled?: boolean;
  currentLayout?: string;
  availableLayouts?: string[];
  enabledLanguages?: string[];
  allowedApps?: string[];
  runningApps?: string[];
  defaultLanguage?: string;
  confidenceThreshold?: number;
  showIndicator?: boolean;
}

const $ = <T extends HTMLElement>(id: string): T => {
  const element = document.getElementById(id);
  if (!element) throw new Error(`Missing element #${id}`);
  return element as T;
};

const LABELS: Record<string, string> = {
  'he-IL': 'Hebrew',
  'en-US': 'English',
  'ru-RU': 'Russian',
  'ar-SA': 'Arabic',
  'el-GR': 'Greek',
  unknown: 'None',
};

const enabled = $<HTMLInputElement>('enabled');
const defaultLanguage = $<HTMLSelectElement>('default-language');
const threshold = $<HTMLInputElement>('threshold');
const thresholdValue = $<HTMLOutputElement>('threshold-value');
const showIndicator = $<HTMLInputElement>('show-indicator');
const saveStatus = $('save-status');

let loaded = false;

async function askAgent(payload: unknown): Promise<AgentState | null> {
  const response = (await chrome.runtime.sendMessage({ type: 'agent-request', payload })) as {
    ok: boolean;
    reply: AgentState | null;
    error: string | null;
  } | undefined;

  if (!response?.reply || response.reply.type === 'error') {
    $('agent-missing').hidden = false;
    $('agent-error').textContent = response?.error ? `Last error: ${response.error}` : '';
    for (const control of [enabled, defaultLanguage, threshold, showIndicator]) control.disabled = true;
    $<HTMLButtonElement>('clear-data').disabled = true;
    return null;
  }

  $('agent-missing').hidden = true;
  return response.reply;
}

function render(state: AgentState): void {
  for (const control of [enabled, defaultLanguage, threshold, showIndicator]) control.disabled = false;
  $<HTMLButtonElement>('clear-data').disabled = false;

  enabled.checked = state.enabled ?? true;
  defaultLanguage.value = state.defaultLanguage ?? 'unknown';
  showIndicator.checked = state.showIndicator ?? true;

  const percent = Math.round((state.confidenceThreshold ?? 0.7) * 100);
  threshold.value = String(percent);
  thresholdValue.textContent = `${percent}%`;

  $('layout-badge').textContent =
    state.enabled === false ? 'OFF' : (LABELS[state.currentLayout ?? 'unknown'] ?? '—').slice(0, 2).toUpperCase();

  $('agent-version').textContent = state.agentVersion ?? '—';
  $('current-layout').textContent = LABELS[state.currentLayout ?? 'unknown'] ?? '—';

  const available = state.availableLayouts ?? [];
  $('available-layouts').textContent = available.length
    ? available.map((tag) => LABELS[tag] ?? tag).join(', ')
    : 'none detected';

  renderLanguageChoices(available, state.enabledLanguages ?? [], state.defaultLanguage ?? 'unknown');
  renderApplications(state.allowedApps ?? [], state.runningApps ?? []);

  loaded = true;
}

/**
 * The language controls, built from what Windows actually has.
 *
 * Both this and the default-language list are filled from the same source, so neither can offer a
 * language that would fail to apply. A fixed list of two was fine when the product knew two.
 *
 * An empty stored list means every language, which is the default and has to look like every box
 * ticked rather than none - the opposite reading would tell a new user their product is switched
 * off.
 */
function renderLanguageChoices(available: string[], enabledTags: string[], defaultTag: string): void {
  const all = enabledTags.length === 0;
  const container = $('enabled-languages');
  container.textContent = '';

  for (const tag of available) {
    const label = document.createElement('label');
    label.className = 'checkbox';

    const box = document.createElement('input');
    box.type = 'checkbox';
    box.value = tag;
    box.checked = all || enabledTags.includes(tag);
    box.addEventListener('change', () => void save());

    label.append(box, document.createTextNode(` ${LABELS[tag] ?? tag}`));
    container.appendChild(label);
  }

  // Rebuilt here too, so the two lists can never disagree about what exists.
  defaultLanguage.textContent = '';
  const none = document.createElement('option');
  none.value = 'unknown';
  none.textContent = 'Leave the keyboard alone';
  defaultLanguage.appendChild(none);

  for (const tag of available) {
    const option = document.createElement('option');
    option.value = tag;
    option.textContent = LABELS[tag] ?? tag;
    defaultLanguage.appendChild(option);
  }
  defaultLanguage.value = defaultTag;
}

/**
 * The applications the user has allowed, and a way to add another.
 *
 * The picker is filled from what is running, asked for only when this page is open. Nothing about
 * an application that is merely running is stored - the list exists so somebody can point at one,
 * and keeping it would be the record of everything they open that the allowlist avoids.
 */
function renderApplications(allowed: string[], running: string[]): void {
  const list = $('allowed-apps');
  list.textContent = '';

  if (allowed.length === 0) {
    const empty = document.createElement('p');
    empty.className = 'hint';
    empty.textContent = 'None yet. Nothing outside the browser is being watched.';
    list.appendChild(empty);
  }

  for (const app of allowed) {
    const row = document.createElement('span');
    row.className = 'checkbox';

    const remove = document.createElement('button');
    remove.type = 'button';
    remove.textContent = `${app} ✕`;
    remove.title = `Stop watching ${app}`;
    remove.addEventListener('click', () => {
      void askAgent({ type: 'command', protocolVersion: 1, command: 'blockApp', app }).then(() => refresh());
    });

    row.appendChild(remove);
    list.appendChild(row);
  }

  const picker = $<HTMLSelectElement>('app-picker');
  picker.textContent = '';

  const choices = running.filter((app) => !allowed.includes(app));
  if (choices.length === 0) {
    const none = document.createElement('option');
    none.value = '';
    none.textContent = 'Nothing else is running with a window';
    picker.appendChild(none);
    return;
  }

  for (const app of choices) {
    const option = document.createElement('option');
    option.value = app;
    option.textContent = app;
    picker.appendChild(option);
  }
}

function chosenLanguages(): string[] {
  const boxes = [...$('enabled-languages').querySelectorAll<HTMLInputElement>('input[type=checkbox]')];
  const ticked = boxes.filter((b) => b.checked).map((b) => b.value);

  // Every box ticked is stored as "no restriction" rather than as a list, so adding a layout in
  // Windows later is allowed by default instead of silently excluded.
  return ticked.length === boxes.length ? [] : ticked;
}

async function save(): Promise<void> {
  // Guard against the change events that firing render() triggers, which would otherwise write
  // the values back the moment they are loaded.
  if (!loaded) return;

  saveStatus.textContent = 'Saving…';

  const reply = await askAgent({
    type: 'command',
    protocolVersion: 1,
    command: 'setSettings',
    enabled: enabled.checked,
    defaultLanguage: defaultLanguage.value,
    confidenceThreshold: Number(threshold.value) / 100,
    showIndicator: showIndicator.checked,
    enabledLanguages: chosenLanguages(),
  });

  saveStatus.textContent = reply ? 'Saved' : 'Could not save';
  setTimeout(() => (saveStatus.textContent = ''), 2000);
}

$('add-app').addEventListener('click', () => {
  const app = $<HTMLSelectElement>('app-picker').value;
  if (!app) return;
  void askAgent({ type: 'command', protocolVersion: 1, command: 'allowApp', app }).then(() => refresh());
});

threshold.addEventListener('input', () => {
  thresholdValue.textContent = `${threshold.value}%`;
});

for (const control of [enabled, defaultLanguage, showIndicator]) {
  control.addEventListener('change', () => void save());
}
threshold.addEventListener('change', () => void save());

$('clear-data').addEventListener('click', () => {
  const status = $('clear-status');

  // A confirm() is worth the friction here: this is the one irreversible action in the product,
  // and the user cannot get their per-conversation history back.
  if (!window.confirm('Delete every stored conversation preference on this computer? This cannot be undone.')) {
    return;
  }

  void (async () => {
    const reply = await askAgent({ type: 'command', protocolVersion: 1, command: 'clearData' });
    status.textContent = reply ? 'All stored preferences deleted.' : 'Could not reach the agent.';
    if (reply) render(reply);
  })();
});

/**
 * Reads the whole state and redraws.
 *
 * The running-application list is asked for here and nowhere else. An ordinary state query - the
 * one the popup makes constantly - never enumerates anything, so nothing is looked at unless this
 * page is open and looking.
 */
async function refresh(): Promise<void> {
  const state = await askAgent({
    type: 'query',
    protocolVersion: 1,
    query: 'state',
    includeRunningApps: true,
  });
  if (state) render(state);
}

void refresh();
