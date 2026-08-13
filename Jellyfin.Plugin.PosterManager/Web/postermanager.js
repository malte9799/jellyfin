/*
 * Poster Manager - Jellyfin web client injection.
 *
 * Adds a "Posters" button to the image editor dialog. Clicking it opens a modal that
 * searches ThePosterDB / Mediux, lists matching sets, shows a poster grid, and applies
 * the chosen image server-side.
 *
 * Jellyfin exposes no plugin hook for the image editor, so this observes the DOM for
 * dialogs and injects the button into their footer.
 */
(function () {
    'use strict';

    if (window.__posterManagerLoaded) {
        return;
    }
    window.__posterManagerLoaded = true;

    var BTN_CLASS = 'pm-inject-btn';
    var state = { sources: [], activeSource: null, item: null, term: '' };

    /* ---------- Jellyfin API helpers ---------- */

    function apiClient() {
        return (window.ApiClient || (window.Emby && window.Emby.ApiClient));
    }

    function apiUrl(path, params) {
        var client = apiClient();
        return client.getUrl('PosterManager/' + path, params || {});
    }

    function apiGet(path, params) {
        return apiClient().getJSON(apiUrl(path, params));
    }

    function apiPost(path, body) {
        var client = apiClient();
        return client.ajax({
            type: 'POST',
            url: apiUrl(path),
            data: JSON.stringify(body),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    /*
     * Mediux serves images without auth, so the browser fetches those directly.
     * ThePosterDB needs the Cloudflare cookie, which the browser doesn't have, so
     * those go through the server-side proxy instead.
     */
    function thumbUrl(source, url) {
        if (source === 'mediux') {
            return url;
        }
        return apiUrl('Thumbnail', {
            source: source,
            url: url,
            api_key: apiClient().accessToken()
        });
    }

    /* The details page keeps the current item id in the URL hash. */
    function currentItemId() {
        var match = /[?&]id=([a-f0-9-]{32,36})/i.exec(window.location.hash || '');
        return match ? match[1] : null;
    }

    /* ---------- Styles ---------- */

    function injectStyles() {
        if (document.getElementById('pm-styles')) {
            return;
        }
        var css = [
            '.pm-overlay{position:fixed;inset:0;z-index:99998;background:rgba(0,0,0,.85);',
            'display:flex;align-items:center;justify-content:center;}',
            '.pm-modal{background:#101010;color:#fff;width:min(1100px,94vw);height:min(820px,92vh);',
            'border-radius:8px;display:flex;flex-direction:column;overflow:hidden;',
            'box-shadow:0 12px 48px rgba(0,0,0,.6);}',
            '.pm-head{display:flex;align-items:center;gap:.75em;padding:1em 1.25em;',
            'border-bottom:1px solid #2a2a2a;flex-wrap:wrap;}',
            '.pm-title{font-size:1.15em;font-weight:600;margin-right:auto;}',
            '.pm-tabs{display:flex;gap:.5em;}',
            '.pm-tab{padding:.4em .9em;border-radius:999px;border:1px solid #333;background:#1a1a1a;',
            'color:#ccc;cursor:pointer;font-size:.9em;}',
            '.pm-tab.pm-active{background:#00a4dc;border-color:#00a4dc;color:#fff;}',
            '.pm-tab[disabled]{opacity:.4;cursor:not-allowed;}',
            '.pm-search{display:flex;gap:.5em;padding:.85em 1.25em;border-bottom:1px solid #2a2a2a;}',
            '.pm-search input{flex:1;padding:.6em .8em;border-radius:4px;border:1px solid #333;',
            'background:#1a1a1a;color:#fff;font-size:.95em;}',
            '.pm-btn{padding:.6em 1.1em;border-radius:4px;border:0;background:#00a4dc;color:#fff;',
            'cursor:pointer;font-size:.9em;}',
            '.pm-btn.pm-secondary{background:#2a2a2a;}',
            '.pm-body{flex:1;overflow-y:auto;padding:1em 1.25em;}',
            '.pm-set{padding:.75em .9em;border:1px solid #2a2a2a;border-radius:6px;margin-bottom:.6em;',
            'cursor:pointer;background:#161616;}',
            '.pm-set:hover{border-color:#00a4dc;}',
            '.pm-set-title{font-weight:600;}',
            '.pm-set-sub{font-size:.85em;color:#999;margin-top:.2em;}',
            '.pm-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:.85em;}',
            '.pm-card{background:#161616;border:2px solid transparent;border-radius:6px;overflow:hidden;',
            'cursor:pointer;display:flex;flex-direction:column;}',
            '.pm-card:hover{border-color:#00a4dc;}',
            '.pm-card img{width:100%;display:block;aspect-ratio:2/3;object-fit:cover;background:#222;}',
            '.pm-card.pm-wide img{aspect-ratio:16/9;}',
            '.pm-card-meta{padding:.45em .6em;font-size:.78em;color:#aaa;}',
            '.pm-msg{padding:2em 0;text-align:center;color:#999;}',
            '.pm-err{padding:1em;border-radius:6px;background:#3a1414;color:#ff9a9a;margin-bottom:1em;}',
            '.pm-foot{padding:.75em 1.25em;border-top:1px solid #2a2a2a;display:flex;gap:.5em;',
            'align-items:center;}',
            '.pm-foot-note{color:#888;font-size:.85em;margin-right:auto;}'
        ].join('');
        var style = document.createElement('style');
        style.id = 'pm-styles';
        style.textContent = css;
        document.head.appendChild(style);
    }

    /* ---------- Modal ---------- */

    var els = {};

    function closeModal() {
        if (els.overlay && els.overlay.parentNode) {
            els.overlay.parentNode.removeChild(els.overlay);
        }
        els = {};
    }

    function buildModal() {
        injectStyles();

        var overlay = document.createElement('div');
        overlay.className = 'pm-overlay';
        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) {
                closeModal();
            }
        });

        overlay.innerHTML = [
            '<div class="pm-modal">',
            '  <div class="pm-head">',
            '    <div class="pm-title">Posters</div>',
            '    <div class="pm-tabs"></div>',
            '  </div>',
            '  <div class="pm-search">',
            '    <input type="text" placeholder="Search title..." />',
            '    <button class="pm-btn pm-do-search">Search</button>',
            '  </div>',
            '  <div class="pm-body"><div class="pm-msg">Loading...</div></div>',
            '  <div class="pm-foot">',
            '    <span class="pm-foot-note"></span>',
            '    <button class="pm-btn pm-secondary pm-back" style="display:none">Back</button>',
            '    <button class="pm-btn pm-secondary pm-close">Close</button>',
            '  </div>',
            '</div>'
        ].join('');

        document.body.appendChild(overlay);

        els = {
            overlay: overlay,
            tabs: overlay.querySelector('.pm-tabs'),
            input: overlay.querySelector('.pm-search input'),
            search: overlay.querySelector('.pm-do-search'),
            body: overlay.querySelector('.pm-body'),
            note: overlay.querySelector('.pm-foot-note'),
            back: overlay.querySelector('.pm-back'),
            close: overlay.querySelector('.pm-close')
        };

        els.close.addEventListener('click', closeModal);
        els.search.addEventListener('click', runSearch);
        els.input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                runSearch();
            }
        });
        els.back.addEventListener('click', runSearch);

        document.addEventListener('keydown', function onEsc(e) {
            if (e.key === 'Escape') {
                closeModal();
                document.removeEventListener('keydown', onEsc);
            }
        });

        return overlay;
    }

    function setBody(html) {
        els.body.innerHTML = html;
    }

    function showError(message) {
        setBody('<div class="pm-err">' + escapeHtml(message) + '</div>');
    }

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text == null ? '' : String(text);
        return div.innerHTML;
    }

    function renderTabs() {
        els.tabs.innerHTML = '';
        state.sources.forEach(function (source) {
            var tab = document.createElement('button');
            tab.className = 'pm-tab' + (source.id === state.activeSource ? ' pm-active' : '');
            tab.textContent = source.name;
            if (!source.configured) {
                tab.disabled = true;
                tab.title = source.name + ' is not configured in the plugin settings.';
            }
            tab.addEventListener('click', function () {
                state.activeSource = source.id;
                renderTabs();
                runSearch();
            });
            els.tabs.appendChild(tab);
        });
    }

    /* ---------- Flow: search -> sets -> posters -> apply ---------- */

    function runSearch() {
        els.back.style.display = 'none';
        state.term = els.input.value.trim();

        var source = state.sources.filter(function (s) { return s.id === state.activeSource; })[0];
        if (!source) {
            return;
        }
        if (!source.configured) {
            showError(source.name + ' is not configured. Add its credentials in Dashboard > Plugins > Poster Manager.');
            return;
        }

        /*
         * Mediux resolves by TMDB ID only; a missing ID can't be worked around by typing.
         * Collections are the exception: the server derives their ID from a child movie,
         * so the box set itself having no TMDB ID is fine.
         */
        if (source.id === 'mediux' && !state.item.tmdbId && state.item.kind !== 'collection') {
            showError('Mediux looks items up by TMDB ID, and this item has no TMDB ID set. '
                + 'Identify the item against TMDB first, then try again.');
            return;
        }

        setBody('<div class="pm-msg">Searching ' + escapeHtml(source.name) + '...</div>');

        apiGet('Search', {
            itemId: state.item.id,
            source: state.activeSource,
            term: state.term
        }).then(renderSets, function (err) {
            showError(errorText(err, 'Search failed.'));
        });
    }

    function renderSets(sets) {
        if (!sets || !sets.length) {
            setBody('<div class="pm-msg">No matches found. Try a different search term — '
                + 'sites use English titles, which often differ from your local title.</div>');
            return;
        }

        els.body.innerHTML = '';
        els.note.textContent = sets.length + ' result' + (sets.length === 1 ? '' : 's');

        sets.forEach(function (set) {
            var row = document.createElement('div');
            row.className = 'pm-set';

            var parts = [];
            if (set.author) { parts.push('by ' + set.author); }
            if (set.year) { parts.push(set.year); }
            if (set.subtitle) { parts.push(set.subtitle); }

            row.innerHTML = '<div class="pm-set-title">' + escapeHtml(set.title) + '</div>'
                + (parts.length ? '<div class="pm-set-sub">' + escapeHtml(parts.join(' · ')) + '</div>' : '');

            row.addEventListener('click', function () { openSet(set); });
            els.body.appendChild(row);
        });
    }

    function openSet(set) {
        setBody('<div class="pm-msg">Loading posters...</div>');
        els.back.style.display = '';

        apiGet('Posters', {
            itemId: state.item.id,
            source: state.activeSource,
            setId: set.id
        }).then(function (images) {
            renderPosters(images, set);
        }, function (err) {
            showError(errorText(err, 'Could not load posters.'));
        });
    }

    function renderPosters(images, set) {
        if (!images || !images.length) {
            setBody('<div class="pm-msg">This set has no images.</div>');
            return;
        }

        els.body.innerHTML = '';
        els.note.textContent = set.title + ' — ' + images.length + ' image'
            + (images.length === 1 ? '' : 's');

        var grid = document.createElement('div');
        grid.className = 'pm-grid';

        images.forEach(function (image) {
            var card = document.createElement('div');
            card.className = 'pm-card'
                + (image.imageType === 'backdrop' || image.imageType === 'titlecard' ? ' pm-wide' : '');

            var img = document.createElement('img');
            img.loading = 'lazy';
            img.src = thumbUrl(image.sourceId, image.thumbnailUrl);
            img.addEventListener('error', function () { img.style.visibility = 'hidden'; });

            var meta = document.createElement('div');
            meta.className = 'pm-card-meta';
            meta.textContent = describeImage(image);

            card.appendChild(img);
            card.appendChild(meta);
            card.addEventListener('click', function () { applyImage(image); });
            grid.appendChild(card);
        });

        els.body.appendChild(grid);
    }

    function describeImage(image) {
        var label = {
            poster: 'Poster',
            backdrop: 'Backdrop',
            season_poster: 'Season',
            titlecard: 'Titlecard'
        }[image.imageType] || 'Poster';

        if (image.seasonNumber != null) {
            label += ' S' + image.seasonNumber;
            if (image.episodeNumber != null) {
                label += 'E' + image.episodeNumber;
            }
        }
        if (image.language && image.language !== 'English') {
            label += ' · ' + image.language;
        }
        return label;
    }

    function applyImage(image) {
        setBody('<div class="pm-msg">Applying...</div>');

        apiPost('Apply', {
            ItemId: state.item.id,
            Source: image.sourceId,
            Url: image.fullUrl,
            ImageType: image.imageType
        }).then(function () {
            setBody('<div class="pm-msg">Applied. Reloading...</div>');
            setTimeout(function () {
                closeModal();
                window.location.reload();
            }, 600);
        }, function (err) {
            showError(errorText(err, 'Could not apply the image.'));
        });
    }

    function errorText(err, fallback) {
        try {
            if (err && typeof err.responseText === 'string' && err.responseText) {
                var parsed = JSON.parse(err.responseText);
                if (parsed && parsed.error) {
                    return parsed.error;
                }
            }
        } catch (e) { /* fall through to the generic message */ }
        return fallback;
    }

    /* ---------- Entry point ---------- */

    function openPosterBrowser() {
        var itemId = currentItemId();
        if (!itemId) {
            return;
        }

        buildModal();
        setBody('<div class="pm-msg">Loading...</div>');

        Promise.all([
            apiGet('Item/' + itemId),
            apiGet('Sources')
        ]).then(function (results) {
            state.item = results[0];
            state.sources = results[1] || [];

            var configured = state.sources.filter(function (s) { return s.configured; });
            state.activeSource = (configured[0] || state.sources[0] || {}).id;

            els.input.value = state.item.name || '';
            renderTabs();

            if (!configured.length) {
                showError('No poster source is configured. Add a ThePosterDB cookie or a Mediux '
                    + 'API token in Dashboard > Plugins > Poster Manager.');
                return;
            }

            runSearch();
        }, function (err) {
            showError(errorText(err, 'Could not load item details.'));
        });
    }

    /* ---------- Button injection ---------- */

    function injectButton(dialog) {
        var footer = dialog.querySelector('.formDialogFooter');
        if (!footer || footer.querySelector('.' + BTN_CLASS)) {
            return;
        }

        // Only the image editor: it has a save button or an image-type selector.
        var isImageEditor = dialog.querySelector('.btnSave')
            || dialog.querySelector('[is="emby-select"][label*="Image"]')
            || /image/i.test(dialog.textContent.slice(0, 400));
        if (!isImageEditor) {
            return;
        }

        if (!currentItemId()) {
            return;
        }

        var button = document.createElement('button');
        button.type = 'button';
        button.className = BTN_CLASS + ' raised button-submit block btnOptions emby-button';
        button.textContent = 'Posters';
        button.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            openPosterBrowser();
        });

        footer.insertBefore(button, footer.firstChild);
    }

    function scan(node) {
        if (!node || node.nodeType !== 1) {
            return;
        }
        if (node.classList && node.classList.contains('dialogContainer')) {
            injectButton(node);
        }
        var containers = node.querySelectorAll ? node.querySelectorAll('.dialogContainer') : [];
        Array.prototype.forEach.call(containers, injectButton);
    }

    new MutationObserver(function (mutations) {
        mutations.forEach(function (mutation) {
            Array.prototype.forEach.call(mutation.addedNodes, scan);
        });
    }).observe(document.body, { childList: true, subtree: true });

    scan(document.body);
})();
