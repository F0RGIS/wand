// bridge/tsconfig.json targets the Node-side bridge and so omits the DOM lib.
// This suite drives a renderer script under vitest's jsdom environment, so it
// pulls the DOM types in locally rather than widening the project config.
/// <reference lib="dom" />

import { afterAll, describe, expect, it } from 'vitest';

const REMOTE_URL = 'http://192.168.1.5:8080/';

/** Lets the script's setTimeout(0) refresh loop run for a few turns. */
async function settle(turns = 8) {
  for (let index = 0; index < turns; index += 1) {
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
}

describe('remote popup cleanup', () => {
  afterAll(() => {
    document.body.innerHTML = '';
  });

  it('leaves the remote link alone once it matches, so the refresh loop settles', async () => {
    document.body.innerHTML =
      '<remote-tooltip><a href="http://stale.invalid/">stale</a></remote-tooltip>';
    (globalThis as Record<string, unknown>).__wandRemoteBridgeUrl = REMOTE_URL;

    // Installs on import: runs one refresh, then observes documentElement.
    await import('../scripts/default/remote-popup-cleanup.js');
    await settle();

    const anchor = document.querySelector('remote-tooltip a[href]');
    expect(anchor?.getAttribute('href')).toBe(REMOTE_URL);
    expect(anchor?.textContent).toBe('http://192.168.1.5:8080');

    // The script's own refresh happens before it starts observing, so the loop
    // needs one external mutation to kick it off. After this the link already
    // holds the right value, so a correct updateLinks writes nothing further.
    document.body.appendChild(document.createElement('div'));
    await settle(2);

    // An unguarded `anchor.textContent = ...` replaces the child text node on
    // every pass. That is a childList record on documentElement, which
    // re-triggers the script's observer, which schedules another refresh --
    // a loop that never quiesces while a remote-tooltip anchor is present.
    let mutationCount = 0;
    const probe = new MutationObserver((records) => {
      mutationCount += records.length;
    });
    probe.observe(document.documentElement, { childList: true, subtree: true });

    await settle();
    probe.disconnect();

    expect(mutationCount).toBe(0);
  });
});
