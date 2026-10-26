/*
 * Jellycord web integration, added to jellyfin-web's index.html by the plugin (IndexHtmlMiddleware).
 * Adds "Discord status" to the Settings page and renders a native-looking
 * settings page at #/jellycord (inside jellyfin-web's fallback page).
 */
(function () {
    'use strict';
    if (window.__jellycord) { return; }
    window.__jellycord = true;

    var ROUTE = '#/jellycord';
    var LABEL = 'Discord status';
    var ROOT_ID = 'jellycordPage';
    var PAGE_CLASSES = ['userPreferencesPage', 'noSecondaryNavPage'];
    // Material "graphic_eq": sound bars, matching Jellycord's status badge
    var ICON = 'graphic_eq';

    var pollTimer = null;
    var standaloneTimer = null;

    function client() { return window.ApiClient; }

    function api(method, path, body) {
        var opts = { type: method, url: client().getUrl(path), dataType: 'json' };
        if (body !== undefined) {
            opts.data = JSON.stringify(body);
            opts.contentType = 'application/json';
        }
        return client().ajax(opts);
    }

    /** The server's { error } message from a failed request, or the fallback. */
    function errorText(err, fallback) {
        if (err && typeof err.json === 'function') {
            return err.json().then(function (b) { return (b && b.error) || fallback; }, function () { return fallback; });
        }
        return Promise.resolve(fallback);
    }

    function esc(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function norm(id) { return String(id || '').replace(/-/g, '').toLowerCase(); }

    // ------------------------------------------------------------------ settings entry

    function injectSettingsLink() {
        // the user's own section of Settings (Profile ... Controls): ours goes at the end of it
        var anchor = document.querySelector('#myPreferencesMenuPage .lnkSubtitlePreferences')
            || document.querySelector('.lnkSubtitlePreferences') || document.querySelector('.lnkPlaybackPreferences');
        if (!anchor || anchor.parentElement.querySelector('.lnkJellycord')) { return; }
        var link = document.createElement('a');
        link.className = 'emby-button lnkJellycord listItem-border';
        link.href = ROUTE;
        link.style.cssText = 'display:block;margin:0;padding:0;';
        link.innerHTML = '<div class="listItem"><span class="material-icons listItemIcon listItemIcon-transparent ' + ICON +
            '" aria-hidden="true"></span><div class="listItemBody"><div class="listItemBodyText">' + LABEL + '</div></div></div>';
        anchor.parentElement.appendChild(link);
    }

    // ------------------------------------------------------------------ page

    function onJellycordRoute() {
        var h = location.hash;
        return h === ROUTE || h.indexOf(ROUTE + '?') === 0 || h.indexOf(ROUTE + '/') === 0;
    }

    function stopPolling() {
        clearInterval(pollTimer);
        pollTimer = null;
    }

    /** Takes our page out of the fallback page and gives jellyfin-web its own content back. */
    function leavePage(page, root) {
        stopPolling();
        root.remove();
        if (!page) { return; }
        Array.prototype.forEach.call(page.children, function (c) {
            if (c.hasAttribute('data-jellycord-display')) {
                c.style.display = c.getAttribute('data-jellycord-display');
                c.removeAttribute('data-jellycord-display');
            }
        });
        PAGE_CLASSES.forEach(function (cls) {
            if (page.getAttribute('data-jellycord-added-' + cls) === '1') {
                page.classList.remove(cls);
                page.removeAttribute('data-jellycord-added-' + cls);
            }
        });
    }

    function renderPage() {
        var page = document.getElementById('fallbackPage');
        var root = document.getElementById(ROOT_ID);
        if (!onJellycordRoute()) {
            clearTimeout(standaloneTimer);
            standaloneTimer = null;
            if (root) { leavePage(page, root); }
            return;
        }

        if (!client() || root) { return; }
        if (!page || page.classList.contains('hide')) {
            // no page to render into (a jellyfin-web layout we don't know): use the standalone page
            if (!standaloneTimer) {
                standaloneTimer = setTimeout(function () {
                    standaloneTimer = null;
                    if (onJellycordRoute() && !document.getElementById(ROOT_ID)) {
                        location.href = client().getUrl('jellycord/link');
                    }
                }, 3000);
            }
            return;
        }

        clearTimeout(standaloneTimer);
        standaloneTimer = null;

        // jellyfin-web owns the fallback page's children: hide them rather than replace them
        Array.prototype.forEach.call(page.children, function (c) {
            c.setAttribute('data-jellycord-display', c.style.display);
            c.style.display = 'none';
        });
        PAGE_CLASSES.forEach(function (cls) {
            if (!page.classList.contains(cls)) {
                page.classList.add(cls);
                page.setAttribute('data-jellycord-added-' + cls, '1');
            }
        });
        page.setAttribute('data-title', LABEL);
        document.title = LABEL;
        var title = document.querySelector('.skinHeader .pageTitle');
        if (title) { title.textContent = LABEL; }

        root = document.createElement('div');
        root.id = ROOT_ID;
        root.className = 'settingsContainer padded-left padded-right padded-bottom-page';
        root.innerHTML =
            '<div style="margin:0 auto;max-width:54em">' +
            '<div class="verticalSection verticalSection-extrabottompadding">' +
            '  <h2 class="sectionTitle">Discord</h2>' +
            '  <p class="fieldDescription" id="jcIntro">Show what you\'re watching as your Discord status.</p>' +
            '  <div id="jcAccount" style="margin-top:1.2em" aria-live="polite"></div>' +
            '  <p id="jcError" role="alert" style="color:#ff6b6b" hidden></p>' +
            '</div>' +
            '<form id="jcPrivacy" class="verticalSection verticalSection-extrabottompadding" style="display:none">' +
            '  <h2 class="sectionTitle">Privacy</h2>' +
            '  <p class="fieldDescription" style="margin-bottom:1.5em">Private items only show <b>"Watching Jellyfin"</b>: no title, poster or progress.</p>' +
            '  <div class="checkboxContainer checkboxContainer-withDescription">' +
            '    <label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" id="jcAlways" /><span class="checkboxLabel">Keep everything private</span></label>' +
            '    <div class="fieldDescription checkboxFieldDescription">Never show what I\'m playing, only "Watching Jellyfin".</div>' +
            '  </div>' +
            '  <div id="jcRules">' +
            '    <fieldset style="border:0;padding:0;margin:0;min-width:0">' +
            '      <legend class="checkboxListLabel" style="margin-top:1.5em;padding:0">Private libraries</legend>' +
            '      <div id="jcLibs" class="checkboxList paperList" style="padding:.5em 1em"></div>' +
            '    </fieldset>' +
            '    <div class="inputContainer" style="margin-top:2em">' +
            '      <input is="emby-input" type="text" id="jcTags" label="My private tags" maxlength="1000" />' +
            '      <div class="fieldDescription">Comma-separated. Tags on a series or album apply to everything in it.</div>' +
            '    </div>' +
            '    <div class="fieldDescription" id="jcAdminRules"></div>' +
            '  </div>' +
            '  <button is="emby-button" type="submit" class="raised button-submit block btnSave emby-button" style="margin-top:2em"><span>Save</span></button>' +
            '  <p class="fieldDescription" id="jcSaveStatus" role="status"></p>' +
            '</form>' +
            '<div class="fieldDescription" style="margin-top:1em">Discord has no presence-only permission: the one Jellycord uses also lets the app read and manage your friends list. ' +
            'Jellycord only sets your status. You can revoke it any time under <b>Discord → Settings → Authorized Apps</b>, or with Unlink above. ' +
            'Only approve a code you just generated yourself.</div>' +
            '</div>';
        page.appendChild(root);

        root.querySelector('#jcPrivacy').addEventListener('submit', function (e) {
            e.preventDefault();
            savePrivacy(root);
        });
        root.querySelector('#jcAlways').addEventListener('change', function () { syncRules(root); });
        loadAccount(root);
        loadPrivacy(root);
    }

    function setError(root, msg) {
        var el = root.querySelector('#jcError');
        el.textContent = msg || '';
        el.hidden = !msg;
    }

    function loadAccount(root) {
        var box = root.querySelector('#jcAccount');
        api('GET', 'jellycord/me').then(function (me) {
            if (!me.configured) {
                box.innerHTML = '<p>The server admin hasn\'t set up Discord yet.</p>';
                return;
            }
            if (me.linked) {
                box.innerHTML =
                    '<div class="listItem" style="padding-left:0"><span class="material-icons listItemIcon" style="background:#3ecf6e" aria-hidden="true">check</span>' +
                    '<div class="listItemBody"><div class="listItemBodyText">Linked to <b>' + esc(me.discordName) + '</b></div>' +
                    (me.needsRelink ? '<div class="listItemBodyText secondary">Discord rejected the saved link. Link again to resume.</div>' : '') +
                    '</div></div>' +
                    (me.needsRelink ? '<button is="emby-button" type="button" class="raised button-submit emby-button" id="jcLink"><span>Link again</span></button>' : '') +
                    '<button is="emby-button" type="button" class="raised emby-button" id="jcUnlink"><span>Unlink Discord</span></button>';
            } else {
                box.innerHTML =
                    '<p>Not linked yet.</p>' +
                    '<button is="emby-button" type="button" class="raised button-submit emby-button" id="jcLink"><span>Link Discord</span></button>';
            }
            var link = box.querySelector('#jcLink');
            if (link) { link.addEventListener('click', function () { startLink(root); }); }
            var unlink = box.querySelector('#jcUnlink');
            if (unlink) {
                unlink.addEventListener('click', function () {
                    if (!window.confirm('Unlink Discord? Your status stops updating until you link again.')) { return; }
                    unlink.disabled = true;
                    setError(root, '');
                    api('POST', 'jellycord/me/unlink').then(function () { loadAccount(root); }, function (err) {
                        unlink.disabled = false;
                        errorText(err, 'Unlinking failed. Try again.').then(function (m) { setError(root, m); });
                    });
                });
            }
        }, function () {
            box.innerHTML = '<p>Jellycord is not available on this server.</p>';
        });
    }

    function startLink(root) {
        var box = root.querySelector('#jcAccount');
        setError(root, '');
        // open the tab now, while we still have the click: a window.open after the request is often blocked
        var tab = window.open('about:blank', '_blank');
        if (tab) { tab.opener = null; }
        api('POST', 'jellycord/me/link').then(function (p) {
            if (tab) { tab.location.href = p.verificationUri; }
            box.innerHTML =
                '<p>Open Discord and approve this code:</p>' +
                '<div style="font:700 2em/1 monospace;letter-spacing:.15em;padding:.6em 0">' + esc(p.userCode) + '</div>' +
                '<button is="emby-button" type="button" class="raised button-submit emby-button" id="jcOpen"><span>Open discord.com/activate</span></button>' +
                '<p class="fieldDescription">Waiting for approval…</p>';
            box.querySelector('#jcOpen').addEventListener('click', function () { window.open(p.verificationUri, '_blank', 'noopener'); });
            stopPolling();
            pollTimer = setInterval(function () { poll(root); }, 3000);
        }, function (err) {
            if (tab) { tab.close(); }
            errorText(err, 'Discord rejected the link request. Ask the server admin to check the Jellycord settings.')
                .then(function (m) { setError(root, m); });
        });
    }

    function poll(root) {
        if (!document.body.contains(root)) { stopPolling(); return; }
        api('GET', 'jellycord/me/link').then(function (r) {
            if (r.status === 'pending') { return; }
            stopPolling();
            if (r.status === 'failed') {
                setError(root, 'Linking failed' + (r.error ? ': ' + r.error : '.') + ' Try again.');
            } else if (r.status === 'expired') {
                setError(root, 'The code expired before it was approved. Try again.');
            } else if (r.status === 'none') {
                setError(root, 'The link request was lost, for example because the server restarted. Try again.');
            }
            loadAccount(root);
        }, function (err) {
            stopPolling();
            errorText(err, 'Lost contact with the server while waiting for Discord. Try again.')
                .then(function (m) { setError(root, m); });
            loadAccount(root);
        });
    }

    function loadPrivacy(root) {
        var userId = client().getCurrentUserId();
        Promise.all([api('GET', 'jellycord/me/settings'), api('GET', 'UserViews?userId=' + encodeURIComponent(userId))]).then(function (r) {
            var s = r[0].settings, admin = r[0].admin, views = r[1].Items || [];
            root.querySelector('#jcIntro').textContent = 'Show what you\'re watching on ' + (r[0].serverName || 'Jellyfin') + ' as your Discord status.';
            var adminLibs = (admin.privateLibraryIds || []).map(norm);
            var mine = (s.PrivateLibraryIds || []).map(norm);
            root.querySelector('#jcAlways').checked = !!s.AlwaysPrivate;
            root.querySelector('#jcTags').value = s.PrivateTags || '';
            root.querySelector('#jcLibs').innerHTML = views.length ? views.map(function (v) {
                var forced = adminLibs.indexOf(norm(v.Id)) >= 0;
                return '<label class="checkboxContainer"><input type="checkbox" is="emby-checkbox" data-lib="' + esc(v.Id) + '"' +
                    (forced || mine.indexOf(norm(v.Id)) >= 0 ? ' checked' : '') + (forced ? ' disabled' : '') +
                    ' /><span class="checkboxLabel">' + esc(v.Name) + (forced ? ' <span class="fieldDescription">(private for everyone)</span>' : '') + '</span></label>';
            }).join('') : '<div class="fieldDescription">No libraries.</div>';
            var rules = [];
            if (admin.privateTags) { rules.push('tags: ' + admin.privateTags); }
            if (admin.privateRatings) { rules.push('ratings: ' + admin.privateRatings); }
            root.querySelector('#jcAdminRules').textContent = rules.length ? 'Always private on this server (' + rules.join('; ') + ').' : '';
            syncRules(root);
            root.querySelector('#jcPrivacy').style.display = '';
        }, function () {
            // the form stays hidden: saving it without the current settings would overwrite them
            setError(root, 'Your privacy settings could not be loaded. Reload the page to try again.');
        });
    }

    function syncRules(root) {
        var rules = root.querySelector('#jcRules');
        rules.style.opacity = root.querySelector('#jcAlways').checked ? '.4' : '1';
    }

    function savePrivacy(root) {
        var btn = root.querySelector('.btnSave');
        var status = root.querySelector('#jcSaveStatus');
        var libs = Array.prototype.map.call(root.querySelectorAll('#jcLibs input:checked:not(:disabled)'), function (c) { return c.getAttribute('data-lib'); });
        btn.disabled = true;
        status.textContent = '';
        api('POST', 'jellycord/me/settings', {
            AlwaysPrivate: root.querySelector('#jcAlways').checked,
            PrivateLibraryIds: libs,
            PrivateTags: root.querySelector('#jcTags').value
        }).then(function () {
            status.textContent = 'Saved.';
            btn.disabled = false;
        }, function (err) {
            btn.disabled = false;
            errorText(err, 'Saving failed. Try again.').then(function (m) { status.textContent = m; });
        });
    }

    // ------------------------------------------------------------------ wiring

    var scheduled = false;
    function tick() {
        scheduled = false;
        if (!client()) { return; }
        injectSettingsLink();
        renderPage();
    }

    // Settings pages are swapped in anywhere under <body>, so this
    // watches the whole tree; it is throttled to one cheap check per frame.
    new MutationObserver(function () {
        if (!scheduled) {
            scheduled = true;
            requestAnimationFrame(tick);
        }
    }).observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener('hashchange', tick);
    tick();
})();
