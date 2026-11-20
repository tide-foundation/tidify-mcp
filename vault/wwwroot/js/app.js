import { TideCloak } from 'https://esm.sh/@tidecloak/js@0.14.41';

// Tidecloak settings come from the page URL, e.g. /?url=http://localhost:8080&realm=a&clientId=browser-login-page
const params = new URLSearchParams(location.search);
const config = {
	url: params.get('url')?.replace(/\/+$/, ''), // tidecloak-js appends /realms/..., so no trailing slash
	realm: params.get('realm'),
	clientId: params.get('clientId'),
	fetchAdaptorForServer: params.get('fetchAdaptor') === 'true'
};
const status = document.getElementById('status');

const tidecloak = new TideCloak({ url: config.url, realm: config.realm, clientId: config.clientId });

// First visit: redirects to the Tidecloak sign-in page. When Tidecloak redirects back here, this resolves.
tidecloak
	.init({
		onLoad: 'login-required',
		checkLoginIframe: false,
		pkceMethod: 'S256',
		// Keeps the query string, so the settings are still in the URL when Tidecloak redirects back.
		// Tidecloak returns its result in the #fragment, which tidecloak-js removes afterwards.
		redirectUri: `${location.origin}${location.pathname}${location.search}`,
		useDPoP: {
			mode: 'auto'
		}
	})
	.then(() => {
		status.textContent = `Signed in as ${tidecloak.token}.`;
		if(config.fetchAdaptorForServer){
			// secureFetch turns our Bearer token into DPoP + a proof when DPoP is on, and sends it as is when it's off.
			const result = document.body.appendChild(document.createElement('pre'));
			tidecloak.secureFetch(
				`${config.url}/admin/realms/${config.realm}/vendorResources/get-installations-provider?clientId=${encodeURIComponent("asgard-mcp-vault")}&providerId=keycloak-oidc-keycloak-json`,
				{ headers: { Authorization: `Bearer ${tidecloak.token}` } }
			)
				.then(async response => {
					// Error statuses (401, 403, ...) land here too: fetch only rejects when no response arrives.
					if (response.status === 403) {
						// Signed in, but the token lacks the role/permission this endpoint needs.
						result.textContent = `403 Forbidden: the signed-in user isn't allowed to call this endpoint.\n${await response.text()}`;
						return;
					}
					const text = await response.text();
					result.textContent = `${response.status} ${response.statusText}\n${text}`;
					if (response.ok) {
						const data = btoa(String.fromCharCode(...new TextEncoder().encode(text)));
						await fetch(`/adaptor?data=${encodeURIComponent(data)}`);
					}
				})
				.catch(error => {
					// No response at all: network error, or blocked by CORS.
					result.textContent = `Fetch failed: ${error instanceof Error ? error.message : JSON.stringify(error)}`;
				});
		}
	})
	.catch(error => {
		status.textContent = `Sign-in failed: ${error instanceof Error ? error.message : JSON.stringify(error)}`;
	})
	.finally(() => {
		console.log(tidecloak.token);
	});
