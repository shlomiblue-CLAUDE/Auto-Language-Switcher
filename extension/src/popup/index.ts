import { LANGUAGE_TAGS } from '../shared/languages.js';

/**
 * The popup answers one question: what is the product doing in this conversation, and why.
 *
 * PDR section 6 is explicit that it shows the decision and its reason but never message content,
 * so every string below is built from enum values the Agent returned. There is no code path here
 * that can render anything the user wrote.
 */

interface AgentState {
  type?: string;
  agentVersion?: string;
  enabled?: boolean;
  currentLayout?: string;
  conversationMode?: string;
  rememberedLanguage?: string;
  sitePaused?: boolean;
  availableLayouts?: string[];
  lastDecision?: {
    language?: string;
    confidence?: number;
    outcome?: string;
    source?: string;
    blocker?: string;
    applied?: boolean;
    errorCode?: string;
  };
}

interface ContentState {
  conversationKey: string | null;
  health: { healthy: boolean; missing: string[] };
  adapterVersion: string;
}

const $ = <T extends HTMLElement>(id: string): T => {
  const element = document.getElementById(id);
  if (!element) throw new Error(`Missing element #${id}`);
  return element as T;
};

const LANGUAGE_LABELS: Record<string, string> = {
  [LANGUAGE_TAGS.Hebrew]: 'Hebrew',
  [LANGUAGE_TAGS.English]: 'English',
  unknown: 'Not decided',
};

const SHORT_LABELS: Record<string, string> = {
  [LANGUAGE_TAGS.Hebrew]: 'HE',
  [LANGUAGE_TAGS.English]: 'EN',
  unknown: '—',
};

/**
 * Blockers and sources in the user's terms.
 *
 * The engine's enum names are precise but say nothing to someone who has not read the source, and
 * "no switch happened" without a reason is what makes a tool feel broken rather than careful.
 */
const REASONS: Record<string, string> = {
  Disabled: 'Switching is turned off',
  SitePaused: 'Paused on this site',
  NotForeground: 'The browser is not in front',
  UserTyping: 'You are in the middle of typing',
  ManualCooldown: 'You changed it yourself just now',
  LowConfidence: 'This conversation is too mixed to call',
  NoSignal: 'Not enough of your own messages yet',
  Hysteresis: 'Just switched, waiting a moment',
  AlreadyCorrect: 'Already on the right layout',
  None: '',
};

const SOURCES: Record<string, string> = {
  ManualPin: 'you pinned this conversation',
  ConversationMemory: 'what you usually type here',
  OutgoingMessages: 'your recent messages',
  AllMessagesFallback: 'the conversation as a whole',
  GlobalDefault: 'your default language',
  None: '',
};

let conversationKey: string | null = null;
let site: string | null = null;

/**
 * The match pattern for the tab in front, or null when there is nothing grantable there.
 *
 * Computed during refresh and kept here because `permissions.request` has to be called straight
 * out of the click handler. Awaiting anything first spends the user gesture, and Chrome then
 * rejects the request without showing a prompt.
 */
let sitePattern: string | null = null;

/** Granted at install time, so the popup must not offer to withdraw it. */
const DECLARED_HOSTS = new Set(['web.whatsapp.com']);

/**
 * The one pattern that covers everything, and the way out of approving sites one at a time.
 *
 * It has to match `optional_host_permissions` in the manifest exactly, or Chrome refuses the
 * request outright. Offering it here rather than declaring it as required is the whole point: the
 * install prompt stays limited to WhatsApp Web, and this stays a decision the user makes, once,
 * with Chrome's own dialog in front of them.
 */
const ALL_SITES = '*://*/*';

async function activeTab(): Promise<chrome.tabs.Tab | null> {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  return tab ?? null;
}

async function readContentState(tabId: number): Promise<ContentState | null> {
  try {
    return (await chrome.tabs.sendMessage(tabId, { type: 'query-state' })) as ContentState;
  } catch {
    // No content script on this tab, which simply means the user is not on a supported site.
    return null;
  }
}

async function askAgent(payload: unknown): Promise<{ ok: boolean; reply: AgentState | null; error: string | null }> {
  const response = (await chrome.runtime.sendMessage({ type: 'agent-request', payload })) as {
    ok: boolean;
    reply: AgentState | null;
    error: string | null;
  };
  return response ?? { ok: false, reply: null, error: 'no response' };
}

function renderDisconnected(error: string | null): void {
  $('agent-missing').hidden = false;
  $('status').hidden = true;
  $('agent-error').textContent = error ? `Last error: ${error}` : '';
  $('layout-badge').textContent = 'OFF';

  for (const button of document.querySelectorAll<HTMLButtonElement>('button')) {
    button.disabled = true;
  }
}

function render(state: AgentState): void {
  $('agent-missing').hidden = true;
  $('status').hidden = false;

  const layout = state.currentLayout ?? 'unknown';
  const paused = state.sitePaused === true;
  const mode = state.conversationMode ?? 'Auto';

  $('layout-badge').textContent = !state.enabled ? 'OFF' : paused ? 'PAUSED' : SHORT_LABELS[layout] ?? '—';

  const remembered = state.rememberedLanguage ?? 'unknown';
  $('conversation-language').textContent =
    mode === 'AlwaysHebrew' ? 'Hebrew (pinned)'
    : mode === 'AlwaysEnglish' ? 'English (pinned)'
    : LANGUAGE_LABELS[remembered] ?? 'Not decided';

  const decision = state.lastDecision;
  const confidence = decision?.confidence ?? 0;
  $('confidence').textContent =
    !decision || decision.outcome === 'Suppressed' || confidence === 0
      ? '—'
      : `${Math.round(confidence * 100)}%`;

  $('reason').textContent = describe(state, decision);

  for (const button of document.querySelectorAll<HTMLButtonElement>('.mode')) {
    button.setAttribute('aria-pressed', String(button.dataset.mode === mode));
    button.disabled = false;
  }

  const pause = $<HTMLButtonElement>('pause-site');
  pause.disabled = false;
  pause.setAttribute('aria-pressed', String(paused));
  pause.textContent = paused ? 'Resume on this site' : 'Pause on this site';
}

function describe(state: AgentState, decision: AgentState['lastDecision']): string {
  if (state.enabled === false) return REASONS.Disabled!;
  if (state.sitePaused) return REASONS.SitePaused!;

  // A layout the product wants but Windows does not have is worth saying out loud - the user has
  // to add it in Windows settings, and nothing the extension does will fix it.
  const available = state.availableLayouts ?? [];
  const missing = Object.values(LANGUAGE_TAGS).filter((tag) => !available.includes(tag));
  if (available.length > 0 && missing.length > 0) {
    const names = missing.map((tag) => LANGUAGE_LABELS[tag] ?? tag).join(' and ');
    return `${names} is not installed in Windows`;
  }

  if (!decision) return 'Waiting for a conversation';

  if (decision.errorCode) return `Could not switch (${decision.errorCode})`;

  if (decision.outcome === 'Switch' || decision.outcome === 'NoChange') {
    const source = SOURCES[decision.source ?? 'None'];
    return source ? `Chose ${LANGUAGE_LABELS[decision.language ?? 'unknown'] ?? '—'} from ${source}` : '—';
  }

  return REASONS[decision.blocker ?? 'None'] ?? '—';
}

/**
 * Whether this product is allowed to watch the site in front, and the control to change that.
 *
 * Deliberately worded as watching rather than access. "Read and change all your data" is Chrome's
 * phrase for the permission, and it is technically true and completely misleading about what
 * happens here: the page is read to count letters, and the counts are the only thing that leaves.
 */
async function renderSiteAccess(tab: chrome.tabs.Tab | null): Promise<void> {
  const panel = $('site-access-panel');
  const button = $<HTMLButtonElement>('site-access');
  const detail = $('site-access-detail');
  const allSites = $<HTMLButtonElement>('all-sites');

  const everywhere = await chrome.permissions.contains({ origins: [ALL_SITES] });

  allSites.hidden = false;
  allSites.disabled = false;
  allSites.setAttribute('aria-pressed', String(everywhere));
  allSites.textContent = everywhere ? 'Stop using all sites' : 'Use on all sites';

  // With everything granted there is nothing left for the per-site button to do, and leaving it
  // there implying otherwise would be a lie about what clicking it achieves.
  if (everywhere) {
    sitePattern = null;
    panel.hidden = false;
    button.hidden = true;
    detail.textContent = 'Every site is enabled. It still only reads the box you are typing in.';
    return;
  }

  let url: URL | null = null;
  try {
    url = tab?.url ? new URL(tab.url) : null;
  } catch {
    url = null;
  }

  sitePattern = null;

  if (!tab) {
    panel.hidden = true;
    return;
  }

  /**
   * A tab whose address we cannot read.
   *
   * This is deliberately shown rather than hidden, because hiding it is what went wrong. Chrome
   * withholds `tab.url` from an extension that has neither a host permission for the page nor
   * `activeTab` - which is precisely the state every not-yet-granted site is in. The panel
   * silently disappeared on exactly the sites whose whole purpose was to offer this button, and
   * from the outside that is indistinguishable from the feature not existing.
   *
   * `activeTab` is now requested, so this should not happen. If it ever does, it says so.
   */
  if (!url) {
    panel.hidden = false;
    button.hidden = true;
    detail.textContent = 'Cannot read this tab’s address, so it cannot be offered here.';
    return;
  }

  // Extension pages, chrome:// and the new tab page. No extension may run there, so offering a
  // button that could only fail would be worse than saying nothing.
  if (url.protocol !== 'https:' && url.protocol !== 'http:') {
    panel.hidden = false;
    button.hidden = true;
    detail.textContent = 'Extensions cannot run on this kind of page.';
    return;
  }

  panel.hidden = false;
  sitePattern = `${url.protocol}//${url.hostname}/*`;

  if (DECLARED_HOSTS.has(url.hostname)) {
    detail.textContent = `${url.hostname} works out of the box.`;
    button.hidden = true;
    return;
  }

  const granted = await chrome.permissions.contains({ origins: [sitePattern] });

  button.hidden = false;
  button.disabled = false;
  button.setAttribute('aria-pressed', String(granted));
  button.textContent = granted ? 'Stop using this site' : 'Use on this site';
  detail.textContent = granted
    ? `Watching ${url.hostname}. Only letter counts ever leave the page.`
    : `Not watching ${url.hostname} yet.`;
}

async function refresh(): Promise<void> {
  const tab = await activeTab();
  site = tab?.url ? new URL(tab.url).hostname : null;

  if (tab?.id != null) {
    const content = await readContentState(tab.id);
    conversationKey = content?.conversationKey ?? null;

    // An adapter that has lost the page is a different failure from an absent Agent, and the two
    // need different actions from the user, so they get different panels.
    //
    // Being signed out is a third thing again, and by far the most likely of the three. It looks
    // identical to a redesign from inside the page, so it gets a panel that says only what is
    // known rather than one that blames WhatsApp for a page the user simply has not signed in to.
    const signedOut = content?.health.missing.includes('signedIn') ?? false;
    $('adapter-signed-out').hidden = !signedOut;
    $('adapter-broken').hidden = content ? content.health.healthy || signedOut : true;
  }

  const { reply, error } = await askAgent({
    type: 'query',
    protocolVersion: 1,
    query: 'state',
    conversationKey,
    site,
  });

  if (!reply || reply.type === 'error') {
    renderDisconnected(error ?? (reply as { message?: string } | null)?.message ?? null);
  } else {
    render(reply);
  }

  // Last, because renderDisconnected disables every button on the page. Granting a site is still
  // a reasonable thing to do while the Agent is down - it is the browser's decision, not the
  // Agent's - so this control re-enables itself afterwards.
  await renderSiteAccess(tab);
}

async function send(payload: Record<string, unknown>): Promise<void> {
  const { reply } = await askAgent({ protocolVersion: 1, type: 'command', ...payload });
  if (reply && reply.type !== 'error') render(reply);
  else await refresh();
}

for (const button of document.querySelectorAll<HTMLButtonElement>('.mode')) {
  button.addEventListener('click', () => {
    if (!conversationKey) return;
    void send({ command: 'setMode', conversationKey, mode: button.dataset.mode });
  });
}

$('site-access').addEventListener('click', () => {
  const pattern = sitePattern;
  if (!pattern) return;

  const granted = $('site-access').getAttribute('aria-pressed') === 'true';

  // Called without an await in front of it, on purpose. Chrome only shows the permission prompt
  // while the click is still being handled.
  //
  // And nothing important is hung off the result: Chrome closes this popup to show the prompt, so
  // the code after the request may never run at all. Starting on the tabs that are already open is
  // the service worker's job, driven by permissions.onAdded, which fires either way.
  const change = granted
    ? chrome.permissions.remove({ origins: [pattern] })
    : chrome.permissions.request({ origins: [pattern] });

  void change.then(() => refresh()).catch(() => refresh());
});

$('all-sites').addEventListener('click', () => {
  const granted = $('all-sites').getAttribute('aria-pressed') === 'true';

  // Requested straight from the click for the same reason as the per-site button: Chrome shows
  // the prompt only while the gesture is live, and closes this popup to do it.
  const change = granted
    ? chrome.permissions.remove({ origins: [ALL_SITES] })
    : chrome.permissions.request({ origins: [ALL_SITES] });

  void change.then(() => refresh()).catch(() => refresh());
});

$('pause-site').addEventListener('click', () => {
  if (!site) return;
  const paused = $('pause-site').getAttribute('aria-pressed') === 'true';
  void send({ command: paused ? 'resumeSite' : 'pauseSite', site });
});

void refresh();
