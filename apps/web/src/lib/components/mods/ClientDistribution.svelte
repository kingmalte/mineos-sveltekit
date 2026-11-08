<script lang="ts">
	import { onMount } from 'svelte';
	import ProgressBar from '$lib/components/ProgressBar.svelte';
	import { modal } from '$lib/stores/modal';
	import { formatBytes, formatDate } from '$lib/utils/formatting';
	import type { ClientPackageEntry, InstalledModWithModpack } from '$lib/api/types';
	import { enc, modsUrl, requestJson, uploadWithProgress } from './modApi';

	interface Props {
		serverName: string;
		/** Bumped by the page after mod changes (a moved client-only mod lands here). */
		refreshKey: number;
	}

	let { serverName, refreshKey }: Props = $props();

	let clientPackages = $state<ClientPackageEntry[]>([]);
	let clientPackageLoading = $state(false);
	let clientPackageCreating = $state(false);
	let clientPackageActions = $state<Record<string, boolean>>({});
	let packageFormat = $state<'curseforge' | 'mrpack'>('curseforge');
	let packageMcVersion = $state('');
	let packageModLoader = $state('');
	let packageModLoaderVersion = $state('');
	let showPackageOptions = $state(false);
	let clientMods = $state<InstalledModWithModpack[]>([]);
	let clientModsLoading = $state(false);
	let clientModUploading = $state(false);
	let clientModUploadProgress = $state(0);
	let clientModUploadingFileName = $state('');
	let clientModIsDragging = $state(false);

	onMount(loadClientPackages);

	$effect(() => {
		void refreshKey;
		void loadClientMods();
	});

	async function loadClientPackages() {
		clientPackageLoading = true;
		try {
			clientPackages = await requestJson<ClientPackageEntry[]>(modsUrl(serverName, '/client-packages'));
		} catch (err) {
			console.error('Failed to load client packages:', err);
		} finally {
			clientPackageLoading = false;
		}
	}

	async function loadClientMods() {
		clientModsLoading = true;
		try {
			clientMods = await requestJson<InstalledModWithModpack[]>(modsUrl(serverName, '/client-mods'));
		} catch (err) {
			console.error('Failed to load client mods:', err);
		} finally {
			clientModsLoading = false;
		}
	}

	async function uploadClientMod(file: File) {
		if (!file.name.toLowerCase().endsWith('.jar')) {
			await modal.error(`Only .jar files are supported for client mods. Got: ${file.name}`);
			return;
		}
		clientModUploading = true;
		clientModUploadingFileName = file.name;
		clientModUploadProgress = 0;
		try {
			await uploadWithProgress(modsUrl(serverName, '/client-mods/upload'), file, (p) => (clientModUploadProgress = p));
			await loadClientMods();
		} catch (err) {
			await modal.error(err instanceof Error ? err.message : `Upload failed for ${file.name}`);
		} finally {
			clientModUploading = false;
			clientModUploadProgress = 0;
			clientModUploadingFileName = '';
		}
	}

	async function uploadAll(files: File[]) {
		for (const file of files) {
			await uploadClientMod(file);
		}
	}

	function handleClientModUpload(event: Event) {
		const input = event.target as HTMLInputElement;
		const files = Array.from(input.files || []);
		input.value = '';
		void uploadAll(files);
	}

	function handleClientModDrop(event: DragEvent) {
		event.preventDefault();
		clientModIsDragging = false;
		void uploadAll(Array.from(event.dataTransfer?.files || []));
	}

	async function deleteClientMod(fileName: string) {
		const confirmed = await modal.confirm(`Delete client mod "${fileName}"?`, 'Delete Client Mod');
		if (!confirmed) return;
		try {
			await requestJson(modsUrl(serverName, `/client-mods/${enc(fileName)}`), { method: 'DELETE' },
				'Failed to delete client mod');
			await loadClientMods();
		} catch (err) {
			await modal.error(err instanceof Error ? err.message : 'Delete failed');
		}
	}

	async function createClientPackage() {
		clientPackageCreating = true;
		try {
			const body: Record<string, string> = { format: packageFormat };
			if (packageMcVersion.trim()) body.minecraftVersion = packageMcVersion.trim();
			if (packageModLoader.trim()) body.modLoader = packageModLoader.trim();
			if (packageModLoaderVersion.trim()) body.modLoaderVersion = packageModLoaderVersion.trim();
			await requestJson(modsUrl(serverName, '/client-packages'), { method: 'POST', json: body },
				'Failed to create client package');
			await loadClientPackages();
		} catch (err) {
			await modal.error(err instanceof Error ? err.message : 'Failed to create client package');
		} finally {
			clientPackageCreating = false;
		}
	}

	async function deleteClientPackage(filename: string) {
		const confirmed = await modal.confirm(
			`Delete client package "${filename}"? This cannot be undone.`,
			'Delete Client Package'
		);
		if (!confirmed) return;

		clientPackageActions[filename] = true;
		try {
			await requestJson(modsUrl(serverName, `/client-packages/${enc(filename)}`), { method: 'DELETE' },
				'Failed to delete client package');
			await loadClientPackages();
		} catch (err) {
			await modal.error(err instanceof Error ? err.message : 'Failed to delete client package');
		} finally {
			delete clientPackageActions[filename];
			clientPackageActions = { ...clientPackageActions };
		}
	}

	function getClientPackageShareUrl(filename: string) {
		const path = `/client-packages/${enc(serverName)}/${enc(filename)}`;
		if (typeof window === 'undefined') return path;
		return new URL(path, window.location.origin).toString();
	}

	function downloadClientPackage(filename: string) {
		window.location.href = modsUrl(serverName, `/client-packages/${enc(filename)}/download?raw=1`);
	}

	async function copyClientPackageLink(filename: string) {
		try {
			await navigator.clipboard.writeText(getClientPackageShareUrl(filename));
			await modal.success('Share link copied to clipboard.');
		} catch (err) {
			await modal.error(err instanceof Error ? err.message : 'Failed to copy link');
		}
	}
</script>

<!-- Client Distribution -->
<div class="mods-section">
	<div class="section-header">
		<h3>Client Distribution</h3>
		<p class="muted">
			Manage client-only mods and generate downloadable packages for players to import into their launcher.
		</p>
	</div>

	<!-- Client-Only Mods -->
	<div class="subsection">
		<div class="client-mods-header">
			<h4>Client-Only Mods{#if clientMods.length > 0} <span class="mod-count">({clientMods.length})</span>{/if}</h4>
			<label class="upload-button small">
				<input
					type="file"
					accept=".jar"
					multiple
					onchange={handleClientModUpload}
					disabled={clientModUploading}
				/>
				{clientModUploading ? 'Uploading...' : 'Upload'}
			</label>
		</div>
		<p class="muted">
			These mods are included in client packages but not loaded by the server.
			Useful for minimaps, shaders, performance mods, and visual enhancements.
		</p>

		<!-- svelte-ignore a11y_no_static_element_interactions -->
		<div
			class="upload-drop"
			class:active={clientModIsDragging}
			ondragover={(event) => {
				event.preventDefault();
				clientModIsDragging = true;
			}}
			ondragleave={() => {
				clientModIsDragging = false;
			}}
			ondrop={handleClientModDrop}
		>
			<div class="drop-content">
				<strong>Drag & drop</strong> client mod files here (.jar)
			</div>
		</div>

		{#if clientModUploading}
			<div class="upload-progress-container">
				<div class="progress-header">
					<span class="progress-title">Uploading: {clientModUploadingFileName}</span>
				</div>
				<ProgressBar value={clientModUploadProgress} color="green" size="sm" showLabel />
			</div>
		{/if}

		{#if clientModsLoading}
			<p class="muted">Loading client mods...</p>
		{:else if clientMods.length === 0}
			<p class="muted">No client-only mods yet. They are added automatically when installing modpacks, or you can upload them manually.</p>
		{:else}
			<div class="mod-list">
				<table>
					<thead>
						<tr>
							<th>Name</th>
							<th>Source</th>
							<th>Size</th>
							<th>Modified</th>
							<th>Actions</th>
						</tr>
					</thead>
					<tbody>
						{#each clientMods as mod (mod.fileName)}
							<tr>
								<td>
									<span class:disabled={mod.isDisabled}>{mod.fileName}</span>
								</td>
								<td>
									{#if mod.modpackName}
										<span class="source-badge modpack">{mod.modpackName}</span>
									{:else}
										<span class="source-badge manual">Manual</span>
									{/if}
								</td>
								<td>{formatBytes(mod.sizeBytes)}</td>
								<td>{new Date(mod.modifiedAt).toLocaleString()}</td>
								<td class="actions">
									<a
										class="btn-action"
										href={modsUrl(serverName, `/client-mods/${enc(mod.fileName)}/download`)}
										download={mod.fileName}
									>
										Download
									</a>
									<button
										class="btn-action danger"
										onclick={() => deleteClientMod(mod.fileName)}
									>
										Delete
									</button>
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}
	</div>

	<!-- Generate Package -->
	<div class="subsection">
		<h4>Generate Package</h4>
		<p class="muted">
			Bundles all server mods, client-only mods, configs, resource packs, and other assets into a single file.
			Share the download link with friends.
		</p>

		<div class="format-selector">
			<button class="format-btn" class:active={packageFormat === 'curseforge'} onclick={() => packageFormat = 'curseforge'}>
				CurseForge (.zip)
			</button>
			<button class="format-btn" class:active={packageFormat === 'mrpack'} onclick={() => packageFormat = 'mrpack'}>
				Modrinth (.mrpack)
			</button>
		</div>

		<button class="toggle-options" onclick={() => showPackageOptions = !showPackageOptions}>
			{showPackageOptions ? 'Hide' : 'Show'} Advanced Options
		</button>

		{#if showPackageOptions}
			<div class="package-options">
				<p class="muted" style="margin-bottom: 12px;">
					These are auto-detected when possible. Only fill in if auto-detection fails or you need to override.
				</p>
				<div class="option-row">
					<label for="pkg-mc-version">Minecraft Version</label>
					<input id="pkg-mc-version" type="text" bind:value={packageMcVersion} placeholder="e.g. 1.20.1 (auto-detected)" />
				</div>
				<div class="option-row">
					<label for="pkg-mod-loader">Mod Loader</label>
					<select id="pkg-mod-loader" bind:value={packageModLoader}>
						<option value="">Auto-detect</option>
						<option value="forge">Forge</option>
						<option value="neoforge">NeoForge</option>
						<option value="fabric">Fabric</option>
						<option value="quilt">Quilt</option>
					</select>
				</div>
				<div class="option-row">
					<label for="pkg-loader-version">Loader Version</label>
					<input id="pkg-loader-version" type="text" bind:value={packageModLoaderVersion} placeholder="e.g. 47.2.0 (auto-detected)" />
				</div>
			</div>
		{/if}

		<button class="btn-action generate-btn" onclick={createClientPackage} disabled={clientPackageCreating}>
			{clientPackageCreating ? 'Generating...' : `Generate ${packageFormat === 'mrpack' ? '.mrpack' : 'CurseForge'} Package`}
		</button>

		{#if clientPackageLoading}
			<p class="muted">Loading packages...</p>
		{:else if clientPackages.length === 0}
			<p class="muted">No packages generated yet.</p>
		{:else}
			<div class="mod-list">
				<table>
					<thead>
						<tr>
							<th>Format</th>
							<th>Filename</th>
							<th>Created</th>
							<th>Size</th>
							<th>Actions</th>
						</tr>
					</thead>
					<tbody>
						{#each clientPackages as pkg (pkg.filename)}
							<tr>
								<td>
									<span class="format-badge" class:mrpack={pkg.format === 'mrpack'} class:curseforge={pkg.format === 'curseforge'}>
										{pkg.format === 'mrpack' ? 'Modrinth' : 'CurseForge'}
									</span>
								</td>
								<td>{pkg.filename}</td>
								<td>{formatDate(pkg.time)}</td>
								<td>{formatBytes(pkg.size)}</td>
								<td class="actions">
									<button class="btn-action" onclick={() => downloadClientPackage(pkg.filename)}>
										Download
									</button>
									<button class="btn-action" onclick={() => copyClientPackageLink(pkg.filename)}>
										Copy Link
									</button>
									<button
										class="btn-action danger"
										onclick={() => deleteClientPackage(pkg.filename)}
										disabled={clientPackageActions[pkg.filename]}
									>
										{clientPackageActions[pkg.filename] ? 'Deleting...' : 'Delete'}
									</button>
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
			</div>
		{/if}
	</div>
</div>
