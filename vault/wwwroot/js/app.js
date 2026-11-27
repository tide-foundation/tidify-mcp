import { IAMService } from 'https://esm.sh/@tidecloak/js@0.14.46';

// =====================================================================================================================
// TODO SECURITY - FIX BEFORE THE PROXY USES DELEGATIONS: LOGIN CSRF / SESSION FIXATION THROUGH loginCode
//
// The loginCode in this page's URL decides which proxy session the delegation made here is attached to, and nothing
// checks that the person logging in is the one that session belongs to. An attacker with their own valid account
// at an allowed MCP server can:
//   1. connect through the proxy and get the login error, with a URL carrying THEIR session's loginCode,
//   2. get a victim to open that URL (a link in an email, a chat, an issue...),
//   3. the victim logs in here, and the victim's delegation is attached to the ATTACKER's session.
// From then on, the attacker's MCP client goes through the proxy carrying the victim's delegation.
// Harmless while the delegation only marks a session as logged in. Serious as soon as the proxy acts with it.
// =====================================================================================================================

const params = new URLSearchParams(location.search);
// The bridge sends the saved adaptor once, in the URL. Keep it in localStorage and reload without it, so the
// sign-in never sees it: TideCloak puts the page's URL in the login state, and the enclave's URL carries that
// state twice, which is too long for it (414).
if (params.has('loginAdaptor')) {
	localStorage.setItem('loginAdaptor', params.get('loginAdaptor'));
	params.delete('loginAdaptor');
	location.replace(`?${params}`);
	await new Promise(() => {}); // stop here: everything below runs on the reloaded page
}
const config = {
	url: params.get('url')?.replace(/\/+$/, ''), // the admin API URLs below append /admin/..., so no trailing slash
	realm: params.get('realm'),
	clientId: params.get('clientId'),
	fetchAdaptorForServer: params.get('fetchAdaptor') === 'true',
	provideDelegation: params.get('provideDelegation') === 'true',
	loginAdaptor: localStorage.getItem('loginAdaptor')
};
const status = document.getElementById('status');

// The progress circles in index.html. Only the steps this visit will run are shown.
const stepItem = name => document.querySelector(`#steps [data-step="${name}"]`);
const setStep = (state, ...names) => names.forEach(name => stepItem(name).className = state);
stepItem('adaptors').hidden = !config.fetchAdaptorForServer;
stepItem('enclave').hidden = stepItem('delegation').hidden = !config.provideDelegation;

// The browser client's adapter config: the one the bridge saved from an earlier sign-in (JSON, then base64url),
// or before that, the same three fields from the URL. IAMService maps it onto TideCloak's options itself.
const loginAdaptor = config.loginAdaptor
	? JSON.parse(new TextDecoder().decode(Uint8Array.fromBase64(config.loginAdaptor, { alphabet: 'base64url' })))
	: { 'auth-server-url': config.url, realm: config.realm, resource: config.clientId };
// for the admin API URLs below: an adapter's auth-server-url usually ends in a slash
const authServerUrl = loginAdaptor['auth-server-url'].replace(/\/+$/, '');

IAMService
	.init(loginAdaptor, {
		onLoad: 'login-required',
		checkLoginIframe: false,
		redirectUri: `${location.origin}${location.pathname}${location.search}`, // without loginAdaptor, see the top
		dpopConfig: {
			mode: 'auto'
		}
	})
	.then(async () => {
		try{

			if(config.fetchAdaptorForServer){
				setStep('active', 'adaptors');
				// IAMService.fetch turns our Bearer token into DPoP + a proof when DPoP is on, and sends it as is when it's off.
				const result = document.body.appendChild(document.createElement('pre'));
				const pre_vaultResp = IAMService.fetch(
					`${authServerUrl}/admin/realms/${loginAdaptor.realm}/vendorResources/get-installations-provider?clientId=${encodeURIComponent("asgard-mcp-vault")}&providerId=keycloak-oidc-keycloak-json`,
					{ headers: { Authorization: `Bearer ${IAMService.getToken()}` } }
				);
				const pre_bridgeResp = IAMService.fetch(
					`${authServerUrl}/admin/realms/${loginAdaptor.realm}/vendorResources/get-installations-provider?clientId=${encodeURIComponent(loginAdaptor.resource)}&providerId=keycloak-oidc-keycloak-json`,
					{ headers: { Authorization: `Bearer ${IAMService.getToken()}` } }
				);
				const vaultResp = await pre_vaultResp;
				const bridgeResp = await pre_bridgeResp;
				if (vaultResp.status === 403 || bridgeResp.status === 403) {
					// Signed in, but the token lacks the role/permission this endpoint needs.
					result.textContent = `403 Forbidden: the signed-in user isn't allowed to call this endpoint.\n${await response.text()}`;
					return;
				}
				if(!vaultResp.ok || !bridgeResp.ok){
					// Any other failure (401, 404, 500, ...): show both, since either one may be the cause.
					result.textContent =
						`asgard-mcp-vault: ${vaultResp.status} ${vaultResp.statusText}\n${await vaultResp.text()}\n\n` +
						`${loginAdaptor.resource}: ${bridgeResp.status} ${bridgeResp.statusText}\n${await bridgeResp.text()}`;
					return;
				}
				const vaultAdaptor = await vaultResp.json();
				const bridgeAdaptor = await bridgeResp.json();

				const saved = await fetch('/Provide/Adaptor', {
					method: 'POST',
					headers: { 'Content-Type': 'application/json' },
					body: JSON.stringify({ vault: vaultAdaptor, bridge: bridgeAdaptor })
				});
				// fetch resolves on error statuses too (409, 400, ...), so check before carrying on
				if (!saved.ok) {
					status.textContent = `Saving the adapter configs failed: ${saved.status} ${await saved.text()}`;
					return;
				}
				setStep('done', 'adaptors');
			}

			if(config.provideDelegation){
				// One request does both: tidecloak-js opens the enclave inside it, when the vault asks for a
				// Tide-signed delegation, and IAMService has no hook for that moment. So they spin together.
				setStep('active', 'enclave', 'delegation');
				const delegation = await IAMService.fetch(
					// absolute: the DPoP proof runs new URL() on it, which throws on a relative path
					`${location.origin}/Provide/Delegation`,
					{ headers: { Authorization: `Bearer ${IAMService.getToken()}` } }
				);
				if (!delegation.ok) {
					status.textContent = `Delegation failed: ${delegation.status} ${delegation.statusText}`;
					return;
				}
				setStep('done', 'enclave', 'delegation');
			}

			status.textContent = 'Done. You can close this tab.';
			//window.close(); // usually ignored (the tab wasn't opened by a script), so the message above is what the user sees
		}catch(error){
			status.textContent = `Error: ${error instanceof Error ? error.message : JSON.stringify(error)}`;
		}finally{
			// Each step is marked done once it succeeds, so one still spinning here was cut short by an error.
			document.querySelectorAll('#steps .active').forEach(item => item.className = 'failed');
		}
	})
	.catch(error => {
		// A failed init also drops IAMService's TideCloak instance, so getToken() would throw from here on.
		status.textContent = `Sign-in failed: ${error instanceof Error ? error.message : JSON.stringify(error)}`;
	})
	// localStorage outlives the tab, so drop the adaptor once this sign-in is over: a later visit without one
	// mustn't pick up a stale copy. A redirect away (to sign in) never gets here, so it's kept for the return.
	.finally(() => localStorage.removeItem('loginAdaptor'));
