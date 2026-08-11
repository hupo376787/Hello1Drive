const authority = 'https://login.microsoftonline.com/consumers/oauth2/v2.0';
const tokenKey = 'hello1drive.auth.token';
const pkceKey = 'hello1drive.auth.pkce';
let callbackPromise = null;
let loginRedirectStarted = false;

function redirectUri() {
  // Keep the SPA callback distinct from the desktop loopback redirect.
  // Entra SPA redirect URI: http://localhost:5173/browser-auth
  return `${window.location.origin}/browser-auth`;
}

function encodeBase64Url(bytes) {
  let binary = '';
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
}

function randomString(bytes = 48) {
  const data = new Uint8Array(bytes);
  crypto.getRandomValues(data);
  return encodeBase64Url(data);
}

async function pkceChallenge(verifier) {
  const data = new TextEncoder().encode(verifier);
  const digest = await crypto.subtle.digest('SHA-256', data);
  return encodeBase64Url(new Uint8Array(digest));
}

function requestedScopes(scopes) {
  return `openid profile offline_access ${scopes}`.trim();
}

function readToken() {
  try { return JSON.parse(localStorage.getItem(tokenKey) || 'null'); }
  catch { return null; }
}

function readPkce() {
  let value = null;
  try { value = sessionStorage.getItem(pkceKey); } catch {}
  // Some mobile/privacy browser modes can lose sessionStorage across the Entra redirect.
  // localStorage is only a short-lived fallback and is cleared as soon as the callback ends.
  if (!value) {
    try { value = localStorage.getItem(pkceKey); } catch {}
  }
  try { return JSON.parse(value || 'null'); } catch { return null; }
}

function writePkce(value) {
  const text = JSON.stringify(value);
  try { sessionStorage.setItem(pkceKey, text); } catch {}
  try { localStorage.setItem(pkceKey, text); } catch {}
}

function clearPkce() {
  try { sessionStorage.removeItem(pkceKey); } catch {}
  try { localStorage.removeItem(pkceKey); } catch {}
}

function writeToken(payload) {
  const expiresIn = Number(payload.expires_in || 3600);
  const old = readToken();
  const record = {
    accessToken: payload.access_token,
    refreshToken: payload.refresh_token || old?.refreshToken || '',
    expiresAt: Date.now() + Math.max(30, expiresIn - 60) * 1000
  };
  localStorage.setItem(tokenKey, JSON.stringify(record));
  return record.accessToken;
}

function cleanCallbackUrl() {
  const url = new URL(window.location.href);
  ['code', 'state', 'session_state', 'error', 'error_description'].forEach(k => url.searchParams.delete(k));
  // After the OAuth code is consumed, return the address bar to the app root
  // without reloading the WebAssembly application.
  history.replaceState({}, document.title, '/');
}

async function postToken(params) {
  const response = await fetch(`${authority}/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams(params)
  });
  const payload = await response.json();
  if (!response.ok) {
    throw new Error(payload.error_description || payload.error || `Token request failed (${response.status})`);
  }
  return payload;
}

async function processCallback(clientId, scopes) {
  const url = new URL(window.location.href);
  const error = url.searchParams.get('error');
  if (error) {
    const description = url.searchParams.get('error_description') || error;
    cleanCallbackUrl();
    throw new Error(description);
  }

  const code = url.searchParams.get('code');
  if (!code) return '';

  const state = url.searchParams.get('state') || '';
  const pkce = readPkce();

  if (!pkce || pkce.state !== state || !pkce.verifier) {
    cleanCallbackUrl();
    throw new Error('OAuth state/PKCE 校验失败，请重新登录。');
  }

  const payload = await postToken({
    client_id: clientId,
    grant_type: 'authorization_code',
    code,
    redirect_uri: redirectUri(),
    code_verifier: pkce.verifier,
    scope: requestedScopes(scopes)
  });

  clearPkce();
  cleanCallbackUrl();
  return writeToken(payload);
}

async function refresh(clientId, scopes, refreshToken) {
  try {
    const payload = await postToken({
      client_id: clientId,
      grant_type: 'refresh_token',
      refresh_token: refreshToken,
      scope: requestedScopes(scopes)
    });
    return writeToken(payload);
  } catch {
    localStorage.removeItem(tokenKey);
    return '';
  }
}

export async function getAccessToken(clientId, scopes) {
  // Startup can ask for a token from more than one code path. Consume an OAuth callback
  // as a single-flight operation so the authorization code is never exchanged twice.
  if (!callbackPromise)
    callbackPromise = processCallback(clientId, scopes);
  const callbackToken = await callbackPromise;
  if (callbackToken) return callbackToken;

  const token = readToken();
  if (!token) return '';
  if (token.accessToken && token.expiresAt > Date.now()) return token.accessToken;
  if (token.refreshToken) return await refresh(clientId, scopes, token.refreshToken);
  return '';
}

export async function login(clientId, scopes) {
  const existing = await getAccessToken(clientId, scopes);
  if (existing) return existing;
  if (loginRedirectStarted) return '';
  loginRedirectStarted = true;

  const verifier = randomString(64);
  const challenge = await pkceChallenge(verifier);
  const state = randomString(24);
  writePkce({ verifier, state, createdAt: Date.now() });

  const url = new URL(`${authority}/authorize`);
  url.searchParams.set('client_id', clientId);
  url.searchParams.set('response_type', 'code');
  url.searchParams.set('redirect_uri', redirectUri());
  url.searchParams.set('response_mode', 'query');
  url.searchParams.set('scope', requestedScopes(scopes));
  url.searchParams.set('code_challenge', challenge);
  url.searchParams.set('code_challenge_method', 'S256');
  url.searchParams.set('state', state);

  window.location.assign(url.toString());
  return await new Promise(() => {});
}

export async function logout() {
  localStorage.removeItem(tokenKey);
  clearPkce();
}
