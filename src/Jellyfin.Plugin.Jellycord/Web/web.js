/*
 * Jellycord web integration, injected into jellyfin-web by the File Transformation plugin.
 * Adds "Discord status" to the user menu and the Settings page, and renders a native-looking
 * settings page at #/jellycord (on top of jellyfin-web's fallback page).
 */
(function () {
    'use strict';
    if (window.__jellycord) { return; }
    window.__jellycord = true;

    var ROUTE = '#/jellycord';
    var LABEL = 'Discord status';
    // Material "graphic_eq": sound bars, matching Jellycord's status badge
    var ICON = 'graphic_eq';
    var ICON_PATH = 'M7 18h2V6H7v12zm4 4h2V2h-2v20zm-8-8h2v-4H3v4zm12 4h2V6h-2v12zm4-8v4h2v-4h-2z';

    var pollTimer = null;

    function client() { return window.ApiClient; }

    function api(method, path, body) {
        var opts = { type: method, url: client().getUrl(path), dataType: 'json' };
        if (body !== undefined) {
            opts.data = JSON.stringify(body);
            opts.contentType = 'application/json';
        }
        return client().ajax(opts);
    }

    function esc(s) {
        var d = document.createElement('div');
        d.textContent = s == null ? '' : String(s);
        return d.innerHTML;
    }

    function norm(id) { return String(id || '').replace(/-/g, '').toLowerCase(); }

    // ------------------------------------------------------------------ menu entries

    function injectUserMenu() {
        var menu = document.getElementById('app-user-menu');
        if (!menu || menu.querySelector('.jellycordMenuItem')) { return; }
        var settings = menu.querySelector('a[href="#/mypreferencesmenu"]');
        if (!settings) { return; }

        var item = settings.cloneNode(true);
        item.classList.add('jellycordMenuItem');
        item.setAttribute('href', ROUTE);
        var path = item.querySelector('svg path');
        if (path) { path.setAttribute('d', ICON_PATH); }
        var svg = item.querySelector('svg');
        if (svg) { svg.removeAttribute('data-testid'); }
        var text = item.querySelector('.MuiListItemText-primary') || item.querySelector('span');
        if (text) { text.textContent = LABEL; }
        item.addEventListener('click', function () {
            // the clone has no React handler, so close the menu ourselves
            var backdrop = menu.querySelector('.MuiBackdrop-root');
            if (backdrop) { backdrop.click(); }
        });
        settings.insertAdjacentElement('afterend', item);
    }

    function injectSettingsLink() {
        var anchor = document.querySelector('.lnkSubtitlePreferences') || document.querySelector('.lnkPlaybackPreferences');
        if (!anchor || anchor.parentElement.querySelector('.lnkJellycord')) { return; }
        var link = document.createElement('a');
        link.className = 'emby-button lnkJellycord listItem-border';
        link.href = ROUTE;
        link.style.cssText = 'display:block;margin:0;padding:0;';
        link.innerHTML = '<div class="listItem"><span class="material-icons listItemIcon listItemIcon-transparent ' + ICON +
            '" aria-hidden="true"></span><div class="listItemBody"><div class="listItemBodyText">' + LABEL + '</div></div></div>';
        anchor.insertAdjacentElement('afterend', link);
    }

    // ------------------------------------------------------------------ page

    function onJellycordRoute() { return location.hash.indexOf(ROUTE) === 0; }

    function renderPage() {
        var page = document.getElementById('fallbackPage');
        if (page && !onJellycordRoute() && page.getAttribute('data-jellycord') === '1') {
            // the fallback page is reused for other unknown routes: put "not found" back
            page.removeAttribute('data-jellycord');
            page.innerHTML = '<div class="padded-left padded-right"><h1>Page not found</h1><p>This is not the page you are looking for.</p></div>';
            document.title = 'Page not found';
            return;
        }

        if (!onJellycordRoute() || !client()) { return; }
        if (!page || page.classList.contains('hide') || page.getAttribute('data-jellycord') === '1') { return; }
        page.setAttribute('data-jellycord', '1');
        page.setAttribute('data-title', LABEL);
        page.classList.add('userPreferencesPage', 'noSecondaryNavPage');
        document.title = LABEL;
        var title = document.querySelector('.skinHeader .pageTitle');
        if (title) { title.textContent = LABEL; }

        page.innerHTML =
            '<div class="settingsContainer padded-left padded-right padded-bottom-page"><div style="margin:0 auto;max-width:54em">' +
            '<div class="verticalSection verticalSection-extrabottompadding">' +
            '  <h2 class="sectionTitle">Discord</h2>' +
            '  <p class="fieldDescription" id="jcIntro">Show what you\'re watching as your Discord status.</p>' +
            '  <div id="jcAccount" style="margin-top:1.2em"></div>' +
            '</div>' +
            '<form id="jcPrivacy" class="verticalSection verticalSection-extrabottompadding" style="display:none">' +
            '  <h2 class="sectionTitle">Privacy</h2>' +
            '  <p class="fieldDescription" style="margin-bottom:1.5em">Private items only show <b>"Watching <span class="jcServer">this server</span>"</b>: no title, poster or progress.</p>' +
            '  <div class="checkboxContainer checkboxContainer-withDescription">' +
            '    <label class="emby-checkbox-label"><input type="checkbox" is="emby-checkbox" id="jcAlways" /><span class="checkboxLabel">Keep everything private</span></label>' +
            '    <div class="fieldDescription checkboxFieldDescription">Never show what I\'m playing, only that I\'m on <span class="jcServer">this server</span>.</div>' +
            '  </div>' +
            '  <div id="jcRules">' +
            '    <h3 class="checkboxListLabel" style="margin-top:1.5em">Private libraries</h3>' +
            '    <div id="jcLibs" class="checkboxList paperList" style="padding:.5em 1em"></div>' +
            '    <div class="inputContainer" style="margin-top:2em">' +
            '      <input is="emby-input" type="text" id="jcTags" label="My private tags" />' +
            '      <div class="fieldDescription">Comma-separated. Tags on a series or album apply to everything in it.</div>' +
            '    </div>' +
            '    <div class="fieldDescription" id="jcAdminRules"></div>' +
            '  </div>' +
            '  <button is="emby-button" type="submit" class="raised button-submit block btnSave emby-button" style="margin-top:2em"><span>Save</span></button>' +
            '</form>' +
            '<div class="fieldDescription" style="margin-top:1em">Discord has no presence-only permission: the one Jellycord uses also lets the app read and manage your friends list. ' +
            'Jellycord only sets your status. You can revoke it any time under <b>Discord → Settings → Authorized Apps</b>, or with Unlink above. ' +
            'Only approve a code you just generated yourself.</div>' +
            '</div></div>';

        page.querySelector('#jcPrivacy').addEventListener('submit', function (e) {
            e.preventDefault();
            savePrivacy(page);
        });
        page.querySelector('#jcAlways').addEventListener('change', function () { syncRules(page); });
        loadAccount(page);
        loadPrivacy(page);
    }

    function loadAccount(page) {
        var box = page.querySelector('#jcAccount');
        api('GET', 'Jellycord/Me').then(function (me) {
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
            if (link) { link.addEventListener('click', function () { startLink(page); }); }
            var unlink = box.querySelector('#jcUnlink');
            if (unlink) {
                unlink.addEventListener('click', function () {
                    unlink.disabled = true;
                    api('POST', 'Jellycord/Me/Unlink').catch(function () { }).then(function () { loadAccount(page); });
                });
            }
        }, function () {
            box.innerHTML = '<p>Jellycord is not available on this server.</p>';
        });
    }

    function startLink(page) {
        var box = page.querySelector('#jcAccount');
        api('POST', 'Jellycord/Me/Link').then(function (p) {
            box.innerHTML =
                '<p>Open Discord and approve this code:</p>' +
                '<div style="font:700 2em/1 monospace;letter-spacing:.15em;padding:.6em 0">' + esc(p.userCode) + '</div>' +
                '<button is="emby-button" type="button" class="raised button-submit emby-button" id="jcOpen"><span>Open discord.com/activate</span></button>' +
                '<p class="fieldDescription">Waiting for approval…</p>';
            box.querySelector('#jcOpen').addEventListener('click', function () { window.open(p.verificationUri, '_blank', 'noopener'); });
            window.open(p.verificationUri, '_blank', 'noopener');
            clearInterval(pollTimer);
            pollTimer = setInterval(function () {
                if (!document.body.contains(box)) { clearInterval(pollTimer); return; }
                api('GET', 'Jellycord/Me/Link').then(function (r) {
                    if (r.status === 'pending') { return; }
                    clearInterval(pollTimer);
                    loadAccount(page);
                });
            }, 3000);
        }, function () {
            box.insertAdjacentHTML('beforeend', '<p style="color:#e5484d">Discord rejected the link request. Ask the server admin to check the Jellycord settings.</p>');
        });
    }

    function loadPrivacy(page) {
        var userId = client().getCurrentUserId();
        Promise.all([api('GET', 'Jellycord/Me/Settings'), api('GET', 'UserViews?userId=' + encodeURIComponent(userId))]).then(function (r) {
            var s = r[0].settings, admin = r[0].admin, views = r[1].Items || [];
            page.querySelectorAll('.jcServer').forEach(function (el) { el.textContent = r[0].serverName || 'this server'; });
            page.querySelector('#jcIntro').textContent = 'Show what you\'re watching on ' + (r[0].serverName || 'Jellyfin') + ' as your Discord status.';
            var adminLibs = (admin.privateLibraryIds || []).map(norm);
            var mine = (s.PrivateLibraryIds || []).map(norm);
            page.querySelector('#jcAlways').checked = !!s.AlwaysPrivate;
            page.querySelector('#jcTags').value = s.PrivateTags || '';
            page.querySelector('#jcLibs').innerHTML = views.length ? views.map(function (v) {
                var forced = adminLibs.indexOf(norm(v.Id)) >= 0;
                return '<label class="checkboxContainer"><input type="checkbox" is="emby-checkbox" data-lib="' + esc(v.Id) + '"' +
                    (forced || mine.indexOf(norm(v.Id)) >= 0 ? ' checked' : '') + (forced ? ' disabled' : '') +
                    ' /><span class="checkboxLabel">' + esc(v.Name) + (forced ? ' <span class="fieldDescription">(private for everyone)</span>' : '') + '</span></label>';
            }).join('') : '<div class="fieldDescription">No libraries.</div>';
            var rules = [];
            if (admin.privateTags) { rules.push('tags: ' + admin.privateTags); }
            if (admin.privateRatings) { rules.push('ratings: ' + admin.privateRatings); }
            page.querySelector('#jcAdminRules').textContent = rules.length ? 'Always private on this server (' + rules.join('; ') + ').' : '';
            syncRules(page);
            page.querySelector('#jcPrivacy').style.display = '';
        });
    }

    function syncRules(page) {
        var rules = page.querySelector('#jcRules');
        rules.style.opacity = page.querySelector('#jcAlways').checked ? '.4' : '1';
    }

    function savePrivacy(page) {
        var btn = page.querySelector('.btnSave');
        var libs = Array.prototype.map.call(page.querySelectorAll('#jcLibs input:checked:not(:disabled)'), function (c) { return c.getAttribute('data-lib'); });
        btn.disabled = true;
        api('POST', 'Jellycord/Me/Settings', {
            AlwaysPrivate: page.querySelector('#jcAlways').checked,
            PrivateLibraryIds: libs,
            PrivateTags: page.querySelector('#jcTags').value
        }).then(function () {
            btn.querySelector('span').textContent = 'Saved';
            setTimeout(function () { btn.querySelector('span').textContent = 'Save'; btn.disabled = false; }, 1500);
        }, function () {
            btn.querySelector('span').textContent = 'Save failed, try again';
            btn.disabled = false;
        });
    }

    // ------------------------------------------------------------------ wiring

    var scheduled = false;
    function tick() {
        scheduled = false;
        if (!client()) { return; }
        injectUserMenu();
        injectSettingsLink();
        renderPage();
    }

    new MutationObserver(function () {
        if (!scheduled) {
            scheduled = true;
            requestAnimationFrame(tick);
        }
    }).observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener('hashchange', tick);
    tick();
})();
