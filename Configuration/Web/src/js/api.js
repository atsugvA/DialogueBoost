/*
 * The page's one door to the server.
 *
 * Every call goes through ApiClient where Jellyfin provides it, so the auth header and the base
 * path are its problem and not ours; the plain shapes are the fallback for a page opened outside
 * the dashboard. Callers name a route, never a URL.
 */

var PLUGIN_ID = 'a818318e-49b0-466d-963a-4467c6999b10';

function apiUrl(path) {
    return (typeof ApiClient.getUrl === 'function') ? ApiClient.getUrl(path) : '/' + path;
}

function pluginUrl(route) {
    return apiUrl('Plugins/DialogueBoost/' + route);
}

function getJson(url) {
    return (typeof ApiClient.getJSON === 'function')
        ? ApiClient.getJSON(url)
        : ApiClient.ajax({ type: 'GET', url: url, dataType: 'json' });
}

function getPlugin(route) {
    return Promise.resolve(getJson(pluginUrl(route)));
}

function postPlugin(route) {
    var url = pluginUrl(route);
    if (typeof ApiClient.postAsync === 'function') {
        return Promise.resolve(ApiClient.postAsync(url));
    }
    if (typeof ApiClient.post === 'function') {
        return Promise.resolve(ApiClient.post(url));
    }
    return Promise.resolve(ApiClient.ajax({ type: 'POST', url: url }));
}
