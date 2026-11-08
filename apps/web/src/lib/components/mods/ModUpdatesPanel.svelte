<script lang="ts">
	import { modal } from '$lib/stores/modal';
	import { formatDate } from '$lib/utils/formatting';
	import { enc, modsUrl, requestJson, type ModUpdateCheck } from './modApi';

	interface Props {
		serverName: string;
		onChanged: () => void;
	}

	let { serverName, onChanged }: Props = $props();

	let result = $state<ModUpdateCheck | null>(null);
	let error = $state('');
	let checking = $state(false);
	let updating = $state<Set<string>>(new Set());
	let failures = $state<Record<string, string>>({});

	// Not automatic: it asks Modrinth about every installed jar.
	async function check() {
		checking = true;
		error = '';
		failures = {};
		try {
			result = await requestJson<ModUpdateCheck>(modsUrl(serverName, '/mods/updates'), {},
				'Could not check for updates');
		} catch (err) {
			result = null;
			error = err instanceof Error ? err.message : 'Could not check for updates';
		} finally {
			checking = false;
		}
	}

	async function update(fileName: string): Promise<boolean> {
		updating = new Set([...updating, fileName]);
		try {
			await requestJson(modsUrl(serverName, `/mods/${enc(fileName)}/update`), { method: 'POST' },
				`Could not update ${fileName}`);
			if (result) {
				result = { ...result, updates: result.updates.filter((u) => u.fileName !== fileName) };
			}
			const { [fileName]: _, ...rest } = failures;
			failures = rest;
			return true;
		} catch (err) {
			failures = { ...failures, [fileName]: err instanceof Error ? err.message : 'Update failed' };
			return false;
		} finally {
			const next = new Set(updating);
			next.delete(fileName);
			updating = next;
		}
	}

	async function updateOne(fileName: string) {
		if (await update(fileName)) onChanged();
	}

	async function updateAll() {
		if (!result) return;
		const pending = result.updates.map((u) => u.fileName);
		const confirmed = await modal.confirm(
			`Update ${pending.length} mod${pending.length === 1 ? '' : 's'} to the newest version for this server's loader and Minecraft version?`,
			'Update mods'
		);
		if (!confirmed) return;
		let any = false;
		// One at a time: each replaces a jar and they share the mods folder.
		for (const fileName of pending) {
			any = (await update(fileName)) || any;
		}
		if (any) onChanged();
	}
</script>

<div class="updates">
	<div class="updates-header">
		<h4>Updates</h4>
		<div class="updates-actions">
			{#if result && result.updates.length > 1}
				<button class="btn-action" onclick={updateAll} disabled={updating.size > 0}>
					Update all ({result.updates.length})
				</button>
			{/if}
			<button class="btn-action" onclick={check} disabled={checking || updating.size > 0}>
				{checking ? 'Checking Modrinth...' : 'Check for updates'}
			</button>
		</div>
	</div>

	{#if error}
		<p class="updates-error">{error}</p>
	{:else if result}
		<p class="muted small">
			{result.recognizedOnModrinth} of {result.modsChecked} enabled mods are known to Modrinth and were
			checked for {result.loader} on Minecraft {result.minecraftVersion}.
		</p>
		{#if result.updates.length === 0}
			<p class="updates-ok">✓ Everything Modrinth knows about is up to date.</p>
		{:else}
			<ul class="update-list">
				{#each result.updates as u (u.fileName)}
					<li>
						<div class="update-info">
							<span class="update-file">{u.fileName}</span>
							<span class="update-versions">
								{u.currentVersion} → <strong>{u.latestVersion}</strong>
								<span class="muted">({formatDate(u.published)})</span>
							</span>
							{#if failures[u.fileName]}
								<span class="update-failure">{failures[u.fileName]}</span>
							{/if}
						</div>
						<button class="btn-action" onclick={() => updateOne(u.fileName)} disabled={updating.has(u.fileName)}>
							{updating.has(u.fileName) ? 'Updating...' : 'Update'}
						</button>
					</li>
				{/each}
			</ul>
		{/if}
	{:else}
		<p class="muted small">Finds newer versions of installed mods on Modrinth for this server's loader and Minecraft version.</p>
	{/if}
</div>

<style>
	.updates {
		border: 1px solid #2a2f47;
		border-radius: 12px;
		padding: 14px 16px;
		background: rgba(20, 24, 39, 0.5);
	}

	.updates-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
		gap: 12px;
		flex-wrap: wrap;
		margin-bottom: 8px;
	}

	.updates-header h4 {
		margin: 0;
		font-size: 15px;
	}

	.updates-actions {
		display: flex;
		gap: 8px;
		flex-wrap: wrap;
	}

	.updates-ok {
		margin: 0;
		color: #b7f5a2;
		font-size: 14px;
	}

	.updates-error,
	.update-failure {
		margin: 0;
		color: #ff9f9f;
		font-size: 13px;
	}

	.small {
		font-size: 12px;
		margin: 0 0 8px;
	}

	.update-list {
		list-style: none;
		margin: 0;
		padding: 0;
		display: flex;
		flex-direction: column;
		gap: 6px;
	}

	.update-list li {
		display: flex;
		justify-content: space-between;
		align-items: center;
		gap: 12px;
		padding: 8px 10px;
		border-radius: 8px;
		background: rgba(91, 158, 255, 0.06);
		border: 1px solid rgba(91, 158, 255, 0.2);
	}

	.update-info {
		display: flex;
		flex-direction: column;
		gap: 2px;
		min-width: 0;
	}

	.update-file {
		font-family: 'Courier New', monospace;
		font-size: 13px;
		overflow-wrap: anywhere;
	}

	.update-versions {
		font-size: 12px;
		color: #aab2d3;
	}
</style>
