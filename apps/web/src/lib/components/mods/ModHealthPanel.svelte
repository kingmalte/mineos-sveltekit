<script lang="ts">
	import { modal } from '$lib/stores/modal';
	import { enc, modsUrl, requestJson, type ModCheckResult, type ModIssue } from './modApi';

	interface Props {
		serverName: string;
		/** Bumped by the page after any mod change, to check again. */
		refreshKey: number;
		onChanged: () => void;
	}

	let { serverName, refreshKey, onChanged }: Props = $props();

	let result = $state<ModCheckResult | null>(null);
	let error = $state('');
	let loading = $state(false);
	let moving = $state<string | null>(null);

	$effect(() => {
		void refreshKey;
		void check();
	});

	async function check() {
		loading = true;
		error = '';
		try {
			result = await requestJson<ModCheckResult>(modsUrl(serverName, '/mods/check'), {}, 'Could not check the mods');
		} catch (err) {
			error = err instanceof Error ? err.message : 'Could not check the mods';
		} finally {
			loading = false;
		}
	}

	async function moveToClient(fileName: string) {
		moving = fileName;
		try {
			await requestJson(modsUrl(serverName, `/mods/${enc(fileName)}/move-to-client`), { method: 'POST' },
				`Could not move ${fileName}`);
			onChanged();
		} catch (err) {
			await modal.error(err instanceof Error ? err.message : `Could not move ${fileName}`);
		} finally {
			moving = null;
		}
	}

	const kindLabels: Record<ModIssue['kind'], string> = {
		'client-only': 'Client-only mod',
		'missing-dependency': 'Missing dependency',
		duplicate: 'Installed twice',
		'wrong-loader': 'Wrong loader'
	};

	const analysed = $derived(result?.mods.filter((m) => !m.isDisabled && m.modId).length ?? 0);
</script>

<div class="health" class:has-issues={(result?.issues.length ?? 0) > 0}>
	<div class="health-header">
		<h4>Mod check</h4>
		<button class="btn-action" onclick={check} disabled={loading}>{loading ? 'Checking...' : 'Check again'}</button>
	</div>

	{#if error}
		<p class="health-error">{error}</p>
	{:else if result}
		{#if result.issues.length === 0}
			<p class="health-ok">
				✓ No problems found in {analysed} mod{analysed === 1 ? '' : 's'}
				{#if !result.serverLoader}
					<span class="muted">(choose the mod loader above to also check for wrong-loader mods)</span>
				{/if}
			</p>
		{:else}
			<p class="health-summary">
				{result.issues.length} problem{result.issues.length === 1 ? '' : 's'} that may stop the server from starting:
			</p>
			<ul class="issue-list">
				{#each result.issues as issue, i (i)}
					<li class="issue" class:error={issue.severity === 'error'}>
						<span class="issue-kind">{kindLabels[issue.kind] ?? issue.kind}</span>
						<span class="issue-message">{issue.message}</span>
						{#if issue.kind === 'client-only'}
							{#each issue.fileNames as fileName (fileName)}
								<button class="btn-action" onclick={() => moveToClient(fileName)} disabled={moving !== null}>
									{moving === fileName ? 'Moving...' : 'Move to client mods'}
								</button>
							{/each}
						{/if}
					</li>
				{/each}
			</ul>
			<p class="muted small">
				Read from the mods' own metadata. Version ranges are not checked, and jars without loader
				metadata (libraries) are ignored.
			</p>
		{/if}
	{:else if loading}
		<p class="muted">Checking mods...</p>
	{/if}
</div>

<style>
	.health {
		border: 1px solid #2a2f47;
		border-radius: 12px;
		padding: 14px 16px;
		background: rgba(20, 24, 39, 0.5);
	}

	.health.has-issues {
		border-color: rgba(255, 107, 107, 0.45);
		background: rgba(255, 92, 92, 0.06);
	}

	.health-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
		gap: 12px;
		margin-bottom: 8px;
	}

	.health-header h4 {
		margin: 0;
		font-size: 15px;
	}

	.health-ok {
		margin: 0;
		color: #b7f5a2;
		font-size: 14px;
	}

	.health-error {
		margin: 0;
		color: #ff9f9f;
		font-size: 14px;
	}

	.health-summary {
		margin: 0 0 8px;
		color: #ffb4b4;
		font-size: 14px;
	}

	.issue-list {
		list-style: none;
		margin: 0;
		padding: 0;
		display: flex;
		flex-direction: column;
		gap: 8px;
	}

	.issue {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: 8px;
		padding: 8px 10px;
		border-radius: 8px;
		background: rgba(255, 183, 77, 0.08);
		border: 1px solid rgba(255, 183, 77, 0.25);
		font-size: 13px;
	}

	.issue.error {
		background: rgba(255, 92, 92, 0.08);
		border-color: rgba(255, 107, 107, 0.3);
	}

	.issue-kind {
		font-weight: 600;
		font-size: 11px;
		text-transform: uppercase;
		letter-spacing: 0.03em;
		color: #ffb4b4;
		white-space: nowrap;
	}

	.issue-message {
		flex: 1;
		min-width: 200px;
		color: #dfe3f5;
		overflow-wrap: anywhere;
	}

	.small {
		font-size: 12px;
		margin: 8px 0 0;
	}
</style>
