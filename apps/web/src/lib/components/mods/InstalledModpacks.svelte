<script lang="ts">
	import { modal } from '$lib/stores/modal';
	import { formatBytes } from '$lib/utils/formatting';
	import type { InstalledModWithModpack, InstalledModpack } from '$lib/api/types';
	import { enc, modsUrl, requestJson } from './modApi';

	interface Props {
		serverName: string;
		modpacks: InstalledModpack[];
		grouped: Record<number, InstalledModWithModpack[]>;
		onChanged: () => void;
	}

	let { serverName, modpacks, grouped, onChanged }: Props = $props();

	let uninstalling = $state<number | null>(null);
	let toggling = $state<string | null>(null);

	async function uninstall(modpack: InstalledModpack) {
		const confirmed = await modal.confirm(
			`Uninstall modpack "${modpack.name}"? This removes all ${modpack.modCount} mods from this modpack.`,
			'Uninstall Modpack'
		);
		if (!confirmed) return;

		uninstalling = modpack.id;
		try {
			await requestJson(modsUrl(serverName, `/modpacks/${modpack.id}`), { method: 'DELETE' },
				'Failed to uninstall modpack');
			onChanged();
		} catch (err) {
			await modal.error(err instanceof Error ? err.message : 'Uninstall failed');
		} finally {
			uninstalling = null;
		}
	}

	async function toggle(mod: InstalledModWithModpack) {
		const action = mod.isDisabled ? 'enable' : 'disable';
		toggling = mod.fileName;
		try {
			await requestJson(modsUrl(serverName, `/mods/${enc(mod.fileName)}/${action}`), { method: 'POST' },
				`Could not ${action} ${mod.fileName}`);
		} catch (err) {
			await modal.error(err instanceof Error ? err.message : `Could not ${action} ${mod.fileName}`);
		} finally {
			toggling = null;
			onChanged();
		}
	}
</script>

{#if modpacks.length > 0}
	<div class="subsection">
		<h4>Installed Modpacks</h4>
		<div class="modpack-list">
			{#each modpacks as modpack (modpack.id)}
				<div class="modpack-card">
					<div class="modpack-header">
						{#if modpack.logoUrl}
							<img class="modpack-logo" src={modpack.logoUrl} alt={modpack.name} />
						{:else}
							<div class="modpack-logo-placeholder"></div>
						{/if}
						<div class="modpack-info">
							<h4>{modpack.name}</h4>
							<p class="modpack-meta">
								{modpack.modCount} mods
								{#if modpack.version}
									<span class="separator">|</span>
									{modpack.version}
								{/if}
							</p>
						</div>
						<button class="btn-action danger" onclick={() => uninstall(modpack)} disabled={uninstalling === modpack.id}>
							{uninstalling === modpack.id ? 'Removing...' : 'Uninstall'}
						</button>
					</div>
					{#if grouped[modpack.id]?.length}
						<details class="modpack-mods">
							<summary>View {grouped[modpack.id].length} mods</summary>
							<ul class="mod-file-list">
								{#each grouped[modpack.id] as mod (mod.fileName)}
									<li class:disabled={mod.isDisabled}>
										<label class="mod-toggle" title={mod.isDisabled ? 'Enable mod' : 'Disable mod'}>
											<input
												type="checkbox"
												checked={!mod.isDisabled}
												disabled={toggling === mod.fileName}
												onchange={() => toggle(mod)}
											/>
											<span class="toggle-slider"></span>
										</label>
										<span class="mod-name">{mod.fileName}</span>
										<span class="mod-size">{formatBytes(mod.sizeBytes)}</span>
									</li>
								{/each}
							</ul>
						</details>
					{/if}
				</div>
			{/each}
		</div>
	</div>
{/if}
