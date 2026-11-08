<script lang="ts">
	import ProgressBar from '$lib/components/ProgressBar.svelte';
	import { modal } from '$lib/stores/modal';
	import { formatBytes } from '$lib/utils/formatting';
	import type { InstalledModWithModpack } from '$lib/api/types';
	import { enc, modsUrl, requestJson, uploadWithProgress } from './modApi';

	interface Props {
		serverName: string;
		mods: InstalledModWithModpack[];
		standalone: InstalledModWithModpack[];
		loading: boolean;
		onChanged: () => void;
	}

	let { serverName, mods, standalone, loading, onChanged }: Props = $props();

	let uploading = $state(false);
	let uploadProgress = $state(0);
	let uploadingFileName = $state('');
	let isDragging = $state(false);
	let deletingAll = $state(false);
	let toggling = $state<string | null>(null);

	const validExtensions = ['.jar', '.zip', '.tar', '.tar.gz', '.tgz'];

	type UploadAnswer = { savedFiles?: string[]; clientOnlyFiles?: string[] };

	async function uploadFiles(files: File[]) {
		const clientOnly: string[] = [];
		let changed = false;
		for (const file of files) {
			const lower = file.name.toLowerCase();
			if (!validExtensions.some((ext) => lower.endsWith(ext))) {
				await modal.error(`Only .jar, .zip, .tar and .tar.gz files are supported. Got: ${file.name}`);
				continue;
			}
			uploading = true;
			uploadingFileName = file.name;
			uploadProgress = 0;
			try {
				const answer = await uploadWithProgress<UploadAnswer>(
					modsUrl(serverName, '/mods/upload'),
					file,
					(p) => (uploadProgress = p)
				);
				changed = true;
				clientOnly.push(...(answer?.clientOnlyFiles ?? []));
			} catch (err) {
				await modal.error(err instanceof Error ? err.message : `Upload failed for ${file.name}`);
			} finally {
				uploading = false;
				uploadProgress = 0;
				uploadingFileName = '';
			}
		}

		if (clientOnly.length > 0) {
			// A client-only mod in the server's mods folder usually crashes the next start.
			const move = await modal.confirm(
				`${clientOnly.join(', ')} ${clientOnly.length === 1 ? 'is a client-only mod' : 'are client-only mods'}. ` +
					`Dedicated servers usually crash on start with ${clientOnly.length === 1 ? 'it' : 'them'}. ` +
					`Move ${clientOnly.length === 1 ? 'it' : 'them'} to the client mods instead?`,
				'Client-only mod'
			);
			if (move) {
				for (const fileName of clientOnly) {
					try {
						await requestJson(modsUrl(serverName, `/mods/${enc(fileName)}/move-to-client`), { method: 'POST' },
							`Could not move ${fileName}`);
					} catch (err) {
						await modal.error(err instanceof Error ? err.message : `Could not move ${fileName}`);
					}
				}
			}
		}

		if (changed) onChanged();
	}

	function handleUpload(event: Event) {
		const input = event.target as HTMLInputElement;
		const files = Array.from(input.files || []);
		input.value = '';
		if (files.length) void uploadFiles(files);
	}

	function handleDrop(event: DragEvent) {
		event.preventDefault();
		isDragging = false;
		const files = Array.from(event.dataTransfer?.files || []);
		if (files.length) void uploadFiles(files);
	}

	async function toggleMod(mod: InstalledModWithModpack) {
		const action = mod.isDisabled ? 'enable' : 'disable';
		toggling = mod.fileName;
		try {
			await requestJson(modsUrl(serverName, `/mods/${enc(mod.fileName)}/${action}`), { method: 'POST' },
				`Could not ${action} ${mod.fileName}`);
		} catch (err) {
			// Used to fail silently: the switch just flipped back.
			await modal.error(err instanceof Error ? err.message : `Could not ${action} ${mod.fileName}`);
		} finally {
			toggling = null;
			onChanged();
		}
	}

	async function deleteMod(fileName: string) {
		const confirmed = await modal.confirm(`Delete ${fileName}?`, 'Delete Mod');
		if (!confirmed) return;
		try {
			await requestJson(modsUrl(serverName, `/mods/${enc(fileName)}`), { method: 'DELETE' },
				`Could not delete ${fileName}`);
			onChanged();
		} catch (err) {
			await modal.error(err instanceof Error ? err.message : 'Delete failed');
		}
	}

	async function deleteAllMods() {
		if (standalone.length === 0) return;
		const confirmed = await modal.confirm(
			`Delete all ${standalone.length} standalone mod(s) from this server? This cannot be undone.`,
			'Delete All Mods'
		);
		if (!confirmed) return;

		deletingAll = true;
		try {
			// One request: validated as a whole up front, results per file.
			const result = await requestJson<{ deleted: string[]; failed: { fileName: string; error: string }[] }>(
				modsUrl(serverName, '/mods/delete-batch'),
				{ method: 'POST', json: { fileNames: standalone.map((m) => m.fileName) } },
				'Delete all failed'
			);
			if (result.failed.length > 0) {
				await modal.error(
					`Deleted ${result.deleted.length} mod(s); ${result.failed.length} could not be deleted:\n` +
						result.failed.map((f) => `${f.fileName}: ${f.error}`).join('\n')
				);
			}
		} catch (err) {
			await modal.error(err instanceof Error ? err.message : 'Delete all failed');
		} finally {
			deletingAll = false;
			onChanged();
		}
	}
</script>

<div class="section-header">
	<h3>Server Mods</h3>
	<div class="section-actions">
		{#if standalone.length > 0}
			<button class="delete-all-btn" onclick={deleteAllMods} disabled={deletingAll}
				title={`Delete all ${standalone.length} standalone mods`}>
				{deletingAll ? 'Deleting...' : `Delete All (${standalone.length})`}
			</button>
		{/if}
		<label class="upload-button small">
			<input type="file" accept=".jar,.zip,.tar,.tar.gz,.tgz" multiple onchange={handleUpload} disabled={uploading} />
			{uploading ? 'Uploading...' : 'Upload'}
		</label>
	</div>
</div>

<!-- svelte-ignore a11y_no_static_element_interactions -->
<div
	class="upload-drop"
	class:active={isDragging}
	ondragover={(event) => {
		event.preventDefault();
		isDragging = true;
	}}
	ondragleave={() => (isDragging = false)}
	ondrop={handleDrop}
>
	<div class="drop-content">
		<strong>Drag & drop</strong> mod files here (.jar, .zip, .tar, .tar.gz)
	</div>
</div>

{#if uploading}
	<div class="upload-progress-container">
		<div class="progress-header">
			<span class="progress-title">Uploading: {uploadingFileName}</span>
		</div>
		<ProgressBar value={uploadProgress} color="green" size="sm" showLabel />
		{#if uploadProgress === 100}
			<p class="progress-message">Processing and extracting files...</p>
		{/if}
	</div>
{/if}

<details class="server-mods-details" open={mods.length > 0 && mods.length <= 30}>
	<summary>
		<h4>All Files{#if !loading && mods.length > 0} <span class="mod-count">({mods.length})</span>{/if}</h4>
	</summary>
	{#if loading}
		<p class="muted">Loading mods...</p>
	{:else if mods.length === 0}
		<p class="muted">No server mods installed.</p>
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
					{#each mods as mod (mod.fileName)}
						<tr class:modpack-mod={!!mod.modpackName}>
							<td>
								<label class="mod-toggle" title={mod.isDisabled ? 'Enable mod' : 'Disable mod'}>
									<input
										type="checkbox"
										checked={!mod.isDisabled}
										disabled={toggling === mod.fileName}
										onchange={() => toggleMod(mod)}
									/>
									<span class="toggle-slider"></span>
								</label>
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
								<a class="btn-action" href={modsUrl(serverName, `/mods/${enc(mod.fileName)}/download`)} download={mod.fileName}>
									Download
								</a>
								<button class="btn-action danger" onclick={() => deleteMod(mod.fileName)}>Delete</button>
							</td>
						</tr>
					{/each}
				</tbody>
			</table>
		</div>
	{/if}
</details>
