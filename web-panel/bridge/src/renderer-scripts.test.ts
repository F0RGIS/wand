import { describe, expect, it } from 'vitest';

import {
  findSteamAppId,
  getSteamClientIconUrl,
  normalizeImageUrl,
} from '../scripts/default/installed-apps-sync/artwork.js';
import {
  buildSnapshot,
  resolveInstalledData,
} from '../scripts/default/installed-apps-sync/installed-data.js';
import { resolveQrRenderer } from '../scripts/default/remote-popup-cleanup/qr-renderer.js';

const CLIENT_ICON_URL =
  'https://api-cdn.wemod.com/steam_community/1245620/client_icon/96.webp';

function makeInstalledAppsState(
  correlationId: string,
  app: Record<string, unknown>,
  title: Record<string, unknown> = {}
) {
  return {
    log: () => undefined,
    storeRef: null,
    unavailableTitlesById: {},
    installedAppsService: {
      installedApps: { [correlationId]: app },
      catalog: {
        games: {
          '900': {
            titleId: '500',
            correlationIds: [correlationId],
            displayName: 'Subnautica',
          },
        },
        titles: {
          '500': { name: 'Subnautica', ...title },
        },
      },
      installedVersions: {
        '900': [{ correlationId }],
      },
    },
  };
}

describe('installed-apps renderer script models', () => {
  it('normalizes captured artwork shapes without a Wand runtime', () => {
    expect(normalizeImageUrl({ cover: { imageUrl: '//cdn.example/game.webp' } }))
      .toBe('https://cdn.example/game.webp');
    expect(normalizeImageUrl('file:///local/image.png')).toBeNull();
  });

  it('finds nested Steam metadata and builds the Wand client icon URL', () => {
    const fixture = {
      game: {
        metadata: {
          steam: {
            appId: 1245620,
          },
        },
      },
    };

    expect(findSteamAppId(fixture)).toBe('1245620');
    expect(getSteamClientIconUrl(findSteamAppId(fixture)))
      .toBe('https://api-cdn.wemod.com/steam_community/1245620/client_icon/96.webp');
  });

  it('prefers the Steam client icon over the title\'s own artwork fields', () => {
    const snapshot = buildSnapshot(
      makeInstalledAppsState(
        'steam:1245620',
        { platform: 'steam', sku: '1245620', displayName: 'Subnautica' },
        {
          imageUrl: 'https://cdn.example/subnautica-cover.webp',
          coverUrl: 'https://cdn.example/subnautica-alt.webp',
        }
      )
    );

    expect(snapshot?.apps).toHaveLength(1);
    expect(snapshot?.apps[0].imageUrl).toBe(CLIENT_ICON_URL);
  });

  it('falls back to title artwork when no Steam AppID is resolvable', () => {
    const snapshot = buildSnapshot(
      makeInstalledAppsState(
        'epic:abc',
        { platform: 'epic', sku: 'abc', displayName: 'Subnautica' },
        { imageUrl: 'https://cdn.example/epic-cover.webp' }
      )
    );

    expect(snapshot?.apps[0].imageUrl).toBe('https://cdn.example/epic-cover.webp');
  });

  it('reads installed apps from the store while the service slice is still empty', () => {
    const storeInstalledApps = {
      'steam:1245620': { platform: 'steam', sku: '1245620' },
    };
    const data = resolveInstalledData({
      unavailableTitlesById: {},
      installedAppsService: { installedApps: {} },
      storeRef: {
        state: {
          getValue: () => ({
            installedApps: storeInstalledApps,
            catalog: {},
            installedGameVersions: {},
          }),
        },
      },
    });

    expect(data?.source).toBe('store');
    expect(data?.rawInstalledApps).toBe(storeInstalledApps);
  });

  it('resolves the tree-shaken Wand QR renderer without a create export', () => {
    const renderer = () => undefined;
    const webpackRequire = {
      c: {
        qrCode: { exports: { mo: renderer } },
      },
    };

    expect(resolveQrRenderer(webpackRequire)).toBe(renderer);
  });
});
