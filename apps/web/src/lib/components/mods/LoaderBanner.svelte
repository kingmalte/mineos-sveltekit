<script lang="ts">
	import { onMount } from 'svelte';
	import LoaderVersionManager from '$lib/components/LoaderVersionManager.svelte';
	import { modal } from '$lib/stores/modal';
	import { loaderLogos, loaderNames, modsUrl, requestJson, type LoaderInfo } from './modApi';

	interface Props {
		serverName: string;
		isServerRunning: boolean;
		/** Minecraft version the running server reported, used when the files do not tell. */
		pingVersion: string | null;
		/** Called whenever the loader / Minecraft version is (re)detected or chosen. */
		onChange: (info: LoaderInfo) => void;
	}

	let { serverName, isServerRunning, pingVersion, onChange }: Props = $props();

	let info = $state<LoaderInfo | null>(null);
	let loading = $state(true);
	let picking = $state(false);
	let saving = $state(false);

	const minecraftVersion = $derived(info?.minecraftVersion ?? pingVersion ?? null);

	onMount(detect);

	async function detect() {
		loading = true;
		try {
			info = await requestJson<LoaderInfo>(modsUrl(serverName, '/loader'));
			picking = !info.loader;
			onChange(info);
		} catch {
			info = { loader: null, version: null, minecraftVersion: null, manual: false };
			picking = true;
			onChange(info);
		} finally {
			loading = false;
		}
	}

	// The choice is stored on the server, so every admin and every browser sees it
	// (it used to live in each browser's localStorage).
	async function choose(loader: string | null) {
		saving = true;
		try {
			info = await requestJson<LoaderInfo>(modsUrl(serverName, '/loader'), {
				method: 'PUT',
				json: { loader }
			}, 'Could not save the mod loader');
			picking = !info.loader;
			onChange(info);
		} catch (err) {
			await modal.error(err instanceof Error ? err.message : 'Could not save the mod loader');
		} finally {
			saving = false;
		}
	}
</script>

{#if loading}
	<div class="loader-banner loading">Detecting mod loader...</div>
{:else if picking || !info?.loader}
	<div class="loader-picker">
		<h3>Select your mod loader</h3>
		<p>
			{info?.loader
				? 'Choose which loader this server uses.'
				: "We couldn't detect a mod loader for this server. Choose one to filter compatible mods and check them."}
			The choice is saved for this server.
		</p>
		<div class="loader-options">
			{#each ['forge', 'fabric', 'neoforge', 'quilt'] as loader (loader)}
				<button class="loader-option" onclick={() => choose(loader)} disabled={saving}>
					<img src={loaderLogos[loader]} alt={loaderNames[loader]} class="loader-logo" />
					<span>{loaderNames[loader]}</span>
				</button>
			{/each}
		</div>
		{#if info?.manual}
			<button class="link-btn" onclick={() => (picking = false)}>Keep {loaderNames[info.loader ?? ''] ?? info.loader}</button>
		{/if}
	</div>
{:else}
	<div class="loader-banner">
		<div class="loader-banner-info">
			<img src={loaderLogos[info.loader]} alt={loaderNames[info.loader] ?? info.loader} class="loader-logo" />
			<span class="loader-name">{loaderNames[info.loader] ?? info.loader}</span>
			{#if info.version}
				<span class="loader-version">{info.version}</span>
			{/if}
			<span class="mc-version" class:unknown={!minecraftVersion}
				title={minecraftVersion ? 'Mod searches and update checks use this version' : 'Searches cannot be filtered by game version'}>
				{minecraftVersion ? `Minecraft ${minecraftVersion}` : 'Minecraft version unknown'}
			</span>
			{#if info.manual}
				<span class="manual-note">
					chosen manually ·
					<button class="link-btn" onclick={() => (picking = true)}>change</button>
					·
					<button class="link-btn" onclick={() => choose(null)} disabled={saving}>re-detect</button>
				</span>
			{/if}
		</div>
		<LoaderVersionManager
			{serverName}
			loader={info.loader}
			loaderName={loaderNames[info.loader] ?? info.loader}
			currentVersion={info.version}
			currentMcVersion={minecraftVersion}
			{isServerRunning}
			onUpdated={detect}
		/>
	</div>
{/if}

<style>
	.mc-version {
		font-size: 12px;
		padding: 2px 8px;
		border-radius: 999px;
		background: rgba(91, 158, 255, 0.12);
		border: 1px solid rgba(91, 158, 255, 0.3);
		color: #a5b4fc;
	}

	.mc-version.unknown {
		background: rgba(255, 183, 77, 0.12);
		border-color: rgba(255, 183, 77, 0.35);
		color: #ffd54f;
	}

	.manual-note {
		font-size: 12px;
		color: #8890b1;
	}

	.link-btn {
		background: none;
		border: none;
		padding: 0;
		color: #5b9eff;
		cursor: pointer;
		font: inherit;
		text-decoration: underline;
	}

	.link-btn:disabled {
		opacity: 0.5;
		cursor: not-allowed;
	}
</style>
