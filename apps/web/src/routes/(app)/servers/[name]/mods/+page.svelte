<script lang="ts">
	import { onMount } from 'svelte';
	import { invalidateAll } from '$app/navigation';
	import CurseForgeSearch from '$lib/components/CurseForgeSearch.svelte';
	import ModrinthSearch from '$lib/components/ModrinthSearch.svelte';
	import LoaderBanner from '$lib/components/mods/LoaderBanner.svelte';
	import ModHealthPanel from '$lib/components/mods/ModHealthPanel.svelte';
	import ModUpdatesPanel from '$lib/components/mods/ModUpdatesPanel.svelte';
	import InstalledModpacks from '$lib/components/mods/InstalledModpacks.svelte';
	import ServerModList from '$lib/components/mods/ServerModList.svelte';
	import ClientDistribution from '$lib/components/mods/ClientDistribution.svelte';
	import { modsUrl, requestJson, type LoaderInfo } from '$lib/components/mods/modApi';
	import '$lib/components/mods/mods-page.css';
	import * as api from '$lib/api/client';
	import { modal } from '$lib/stores/modal';
	import type { PageData } from './$types';
	import type { LayoutData } from '../$types';
	import type { InstalledModWithModpack, InstalledModpack, ServerHeartbeat } from '$lib/api/types';

	let { data }: { data: PageData & { server: LayoutData['server'] } } = $props();

	const serverName = $derived(data.server?.name ?? '');
	const isServerRunning = $derived(data.server?.status === 'running');

	let mods = $state<InstalledModWithModpack[]>([]);
	let modpacks = $state<InstalledModpack[]>([]);
	let loading = $state(false);
	let loaderInfo = $state<LoaderInfo | null>(null);
	let pingVersion = $state<string | null>(null);
	// Bumped after any change, so the check and the client mods reload.
	let refreshKey = $state(0);
	// Mod changes apply on the next start; say so instead of blocking them while running.
	let changedThisVisit = $state(false);
	let restarting = $state(false);

	const restartNeeded = $derived(changedThisVisit || !!data.server?.needsRestart);

	// The version mod searches filter by. Read from the server's files first, so a
	// stopped server (when mods are usually installed) still filters; the running
	// server's ping is only the fallback.
	const minecraftVersion = $derived(loaderInfo?.minecraftVersion ?? pingVersion);

	const grouped = $derived.by(() => {
		const byPack: Record<number, InstalledModWithModpack[]> = {};
		const standalone: InstalledModWithModpack[] = [];
		for (const mod of mods) {
			if (mod.modpackId) (byPack[mod.modpackId] ??= []).push(mod);
			else standalone.push(mod);
		}
		return { byPack, standalone };
	});

	onMount(() => {
		void loadMods();
		void loadPingVersion();
	});

	async function loadPingVersion() {
		try {
			const heartbeat = await requestJson<ServerHeartbeat>(modsUrl(serverName, '/heartbeat'));
			pingVersion = heartbeat.ping?.serverVersion || null;
		} catch {
			pingVersion = null;
		}
	}

	async function loadMods() {
		if (!serverName) return;
		loading = true;
		try {
			const [modList, packs] = await Promise.all([
				requestJson<InstalledModWithModpack[]>(modsUrl(serverName, '/mods/with-modpacks')),
				requestJson<InstalledModpack[]>(modsUrl(serverName, '/modpacks'))
			]);
			mods = modList ?? [];
			modpacks = packs ?? [];
		} catch (err) {
			await modal.error(err instanceof Error ? err.message : 'Failed to load mods');
		} finally {
			loading = false;
		}
	}

	function changed() {
		changedThisVisit = true;
		refreshKey += 1;
		void loadMods();
	}

	async function restartNow() {
		restarting = true;
		try {
			const result = await api.restartServer(fetch, serverName);
			if (result.error) {
				await modal.error(result.error);
				return;
			}
			changedThisVisit = false;
			await invalidateAll();
		} finally {
			restarting = false;
		}
	}
</script>

<div class="mods-page">
	<div class="page-header">
		<h2>Mods</h2>
		<p class="subtitle">Upload, manage, and install mods for this server</p>
	</div>

	{#if serverName}
		<LoaderBanner {serverName} {isServerRunning} {pingVersion} onChange={(info) => (loaderInfo = info)} />
	{/if}

	{#if restartNeeded && isServerRunning}
		<div class="restart-banner" role="status">
			<span>Mod changes take effect after the server restarts.</span>
			<button class="btn-action" onclick={restartNow} disabled={restarting}>
				{restarting ? 'Restarting...' : 'Restart now'}
			</button>
		</div>
	{/if}

	<div class="mods-section">
		{#if serverName}
			<ServerModList {serverName} {mods} standalone={grouped.standalone} {loading} onChanged={changed} />

			<div class="tools">
				<ModHealthPanel {serverName} {refreshKey} onChanged={changed} />
				<ModUpdatesPanel {serverName} onChanged={changed} />
			</div>

			<InstalledModpacks {serverName} {modpacks} grouped={grouped.byPack} onChanged={changed} />

			<div class="subsection">
				<h4>Install from CurseForge</h4>
				<CurseForgeSearch {serverName} serverVersion={minecraftVersion} loader={loaderInfo?.loader ?? null} onInstallComplete={changed} />
			</div>

			<div class="subsection">
				<h4>Install from Modrinth</h4>
				<ModrinthSearch {serverName} serverVersion={minecraftVersion} loader={loaderInfo?.loader ?? null} onInstallComplete={changed} />
			</div>
		{/if}
	</div>

	{#if serverName}
		<div class="mods-section">
			<ClientDistribution {serverName} {refreshKey} />
		</div>
	{/if}
</div>

<style>
	.restart-banner {
		display: flex;
		justify-content: space-between;
		align-items: center;
		gap: 12px;
		flex-wrap: wrap;
		padding: 12px 16px;
		border-radius: 12px;
		background: rgba(255, 183, 77, 0.1);
		border: 1px solid rgba(255, 183, 77, 0.4);
		color: #ffe1a6;
		font-size: 14px;
	}

	.tools {
		display: grid;
		grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
		gap: 16px;
	}

	@media (max-width: 480px) {
		.tools {
			grid-template-columns: 1fr;
		}
	}
</style>
