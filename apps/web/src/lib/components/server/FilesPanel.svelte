<script lang="ts">
	import { untrack } from 'svelte';
	import { beforeNavigate } from '$app/navigation';
	import { modal } from '$lib/stores/modal';
	import { formatBytes, formatDate } from '$lib/utils/formatting';
	import {
		baseName,
		fileActionUrl,
		fileApiUrl,
		fileDownloadUrl,
		joinPath,
		normalizePath,
		parentPath
	} from '$lib/utils/filePaths';
	import type { ServerPanelData } from './panelData';

	let { data }: { data: ServerPanelData } = $props();

	type FileEntry = { name: string; isDirectory: boolean; size: number; modified: string };
	type DialogKind = 'new-file' | 'new-folder' | 'move' | 'copy';
	type Dialog = {
		kind: DialogKind;
		source: FileEntry | null;
		value: string;
		overwrite: boolean;
		error: string;
		busy: boolean;
	};

	let currentPath = $state('/');
	let files = $state<FileEntry[]>([]);
	let loading = $state(false);
	let selectedFile = $state<string | null>(null);
	let fileContent = $state('');
	// What the file held when it was opened or last saved; edits are compared to it.
	let originalContent = $state('');
	let editMode = $state(false);
	let uploadError = $state('');
	let dialog = $state<Dialog | null>(null);

	let serverName = $derived(data.server?.name ?? null);
	let isDirty = $derived(editMode && fileContent !== originalContent);

	// Load once per server. loadFiles reads currentPath, so a plain
	// `$effect(() => loadFiles())` re-ran on every folder change on top of the
	// explicit load in navigateTo — two requests per click.
	$effect(() => {
		const name = serverName;
		if (!name) return;
		untrack(() => {
			currentPath = '/';
			closeViewer();
			void loadFiles();
		});
	});

	// Leaving the page (or the tab) with unsaved edits asks first.
	beforeNavigate((navigation) => {
		if (!isDirty) return;
		if (navigation.willUnload) {
			// The browser shows its own "leave site?" prompt.
			navigation.cancel();
			return;
		}
		if (!confirm(`Discard your unsaved changes to "${selectedFile}"?`)) {
			navigation.cancel();
		}
	});

	async function confirmDiscard(): Promise<boolean> {
		if (!isDirty) return true;
		return modal.confirm(
			`You have unsaved changes to "${selectedFile}". Discard them?`,
			'Unsaved changes'
		);
	}

	function closeViewer() {
		selectedFile = null;
		fileContent = '';
		originalContent = '';
		editMode = false;
	}

	async function errorMessage(res: Response, fallback: string): Promise<string> {
		const body = await res.json().catch(() => null);
		return body?.error || body?.detail || fallback;
	}

	/**
	 * Lists `path` and only then makes it the current folder. Switching first left
	 * the old listing on screen under the new path when the folder could not be
	 * opened, so every following click went to the wrong place.
	 */
	async function loadFiles(path: string = currentPath): Promise<boolean> {
		if (!serverName) return false;
		loading = true;
		try {
			const res = await fetch(fileApiUrl(serverName, path));
			if (res.ok) {
				const result = await res.json();
				files = result.entries ?? [];
				currentPath = path;
				return true;
			}
			await modal.error(await errorMessage(res, 'Failed to load files'));
			return false;
		} catch {
			await modal.error('Failed to load files - the server could not be reached.');
			return false;
		} finally {
			loading = false;
		}
	}

	async function openFolder(path: string) {
		if (!(await confirmDiscard())) return;
		if (await loadFiles(path)) closeViewer();
	}

	async function navigateTo(file: FileEntry) {
		if (file.isDirectory) {
			await openFolder(joinPath(currentPath, file.name));
		} else {
			await viewFile(file.name);
		}
	}

	async function navigateUp() {
		if (currentPath === '/') return;
		await openFolder(parentPath(currentPath));
	}

	async function viewFile(name: string) {
		if (!serverName) return;
		if (name !== selectedFile && !(await confirmDiscard())) return;
		loading = true;
		try {
			const res = await fetch(fileApiUrl(serverName, joinPath(currentPath, name)));
			if (res.ok) {
				const result = await res.json();
				if (result.kind === 'file' && result.file) {
					fileContent = result.file.content ?? '';
					originalContent = fileContent;
					selectedFile = name;
					editMode = false;
				} else {
					await modal.error('Selected path is not a file');
				}
			} else {
				await modal.error(await errorMessage(res, 'Failed to read file'));
			}
		} finally {
			loading = false;
		}
	}

	async function cancelEdit() {
		if (!(await confirmDiscard())) return;
		fileContent = originalContent;
		editMode = false;
	}

	async function saveFile() {
		if (!serverName || !selectedFile) return;
		loading = true;
		try {
			const res = await fetch(fileApiUrl(serverName, joinPath(currentPath, selectedFile)), {
				method: 'PUT',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ content: fileContent })
			});
			if (res.ok) {
				originalContent = fileContent;
				editMode = false;
				await loadFiles();
			} else {
				await modal.error(await errorMessage(res, 'Failed to save file'));
			}
		} finally {
			loading = false;
		}
	}

	function onEditorKeydown(event: KeyboardEvent) {
		if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
			event.preventDefault();
			if (editMode && !loading) void saveFile();
		}
	}

	async function deleteFile(file: FileEntry) {
		if (!serverName) return;
		const what = file.isDirectory ? 'folder' : 'file';
		const extra = file.isDirectory ? ' and everything in it' : '';
		const confirmed = await modal.confirm(
			`Delete the ${what} "${file.name}"${extra}? This cannot be undone.`,
			`Delete ${what}`
		);
		if (!confirmed) return;

		loading = true;
		try {
			const res = await fetch(fileApiUrl(serverName, joinPath(currentPath, file.name)), {
				method: 'DELETE'
			});
			if (res.ok) {
				if (selectedFile === file.name) closeViewer();
				await loadFiles();
			} else {
				await modal.error(await errorMessage(res, 'Failed to delete'));
			}
		} finally {
			loading = false;
		}
	}

	async function uploadFile(event: Event) {
		if (!serverName) return;
		const input = event.target as HTMLInputElement;
		const file = input.files?.[0];
		if (!file) return;

		uploadError = '';
		loading = true;
		try {
			// The File itself is the body, so the browser streams it instead of
			// reading it all into memory first. Always octet-stream: the API reads a
			// JSON content type as {content: ...}, which broke uploading .json files.
			const res = await fetch(fileApiUrl(serverName, joinPath(currentPath, file.name)), {
				method: 'POST',
				headers: { 'Content-Type': 'application/octet-stream' },
				body: file
			});

			if (res.ok) {
				await loadFiles();
			} else {
				uploadError = await errorMessage(res, 'Failed to upload file');
			}
		} catch {
			uploadError = 'Upload failed - the connection was interrupted.';
		} finally {
			input.value = '';
			loading = false;
		}
	}

	const dialogTitles: Record<DialogKind, string> = {
		'new-file': 'New file',
		'new-folder': 'New folder',
		move: 'Rename / move',
		copy: 'Copy'
	};

	async function openDialog(kind: DialogKind, source: FileEntry | null = null) {
		// Moving the open file away under unsaved edits would lose them on save.
		if (kind === 'move' && source && source.name === selectedFile && !(await confirmDiscard())) {
			return;
		}
		let value = '';
		if (source) {
			const full = joinPath(currentPath, source.name);
			value = kind === 'copy' ? `${full}${source.isDirectory ? '-copy' : ''}` : full;
		}
		dialog = { kind, source, value, overwrite: false, error: '', busy: false };
	}

	async function submitDialog(event: SubmitEvent) {
		event.preventDefault();
		if (!dialog || !serverName) return;
		const d = dialog;
		const raw = d.value.trim();
		if (!raw) {
			d.error = 'Please enter a name or path.';
			return;
		}

		let url: string;
		let body: Record<string, unknown>;
		let createdPath: string | null = null;
		if (d.kind === 'new-file' || d.kind === 'new-folder') {
			// Relative to the open folder unless it starts with "/".
			createdPath = raw.startsWith('/') ? normalizePath(raw) : normalizePath(joinPath(currentPath, raw));
			url = fileActionUrl(serverName, d.kind === 'new-file' ? 'create-file' : 'create-folder');
			body = { path: createdPath };
		} else {
			const from = joinPath(currentPath, d.source!.name);
			const to = normalizePath(raw);
			if (to === from) {
				dialog = null;
				return;
			}
			url = fileActionUrl(serverName, d.kind);
			body = { from, to, overwrite: d.overwrite };
		}

		d.busy = true;
		d.error = '';
		try {
			const res = await fetch(url, {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify(body)
			});
			if (!res.ok) {
				d.error = await errorMessage(res, 'That did not work.');
				return;
			}
		} catch {
			d.error = 'The request failed - please try again.';
			return;
		} finally {
			d.busy = false;
		}

		dialog = null;
		if (d.kind === 'move' && d.source?.name === selectedFile) closeViewer();
		await loadFiles();
		// A new file in this folder opens straight in the editor.
		if (d.kind === 'new-file' && createdPath && parentPath(createdPath) === currentPath) {
			await viewFile(baseName(createdPath));
			editMode = true;
		}
	}
</script>

<div class="file-browser">
	<div class="toolbar">
		<div class="breadcrumb">
			<button onclick={navigateUp} disabled={currentPath === '/'} class="btn-icon">
				↑ Up
			</button>
			<span class="path">{currentPath}</span>
		</div>
		<div class="toolbar-actions">
			<button onclick={() => openDialog('new-file')} class="btn" disabled={!serverName}>New file</button>
			<button onclick={() => openDialog('new-folder')} class="btn" disabled={!serverName}>
				New folder
			</button>
			<label class="btn upload-btn">
				<input type="file" onchange={uploadFile} hidden />
				Upload
			</label>
			{#if serverName}
				<a
					class="btn"
					href={fileDownloadUrl(serverName, currentPath)}
					download
					title="Download this folder as a .zip"
				>
					Download folder
				</a>
			{/if}
			<button onclick={() => loadFiles()} disabled={loading} class="btn">
				{loading ? 'Loading...' : 'Refresh'}
			</button>
		</div>
	</div>

	<div class="content">
		<div class="file-list">
			<h3>Files & Directories</h3>
			{#if uploadError}
				<p class="error">{uploadError}</p>
			{/if}
			{#if files.length === 0}
				<p class="empty">No files or directories</p>
			{:else}
				<div class="table-scroll">
				<table>
					<thead>
						<tr>
							<th>Name</th>
							<th>Size</th>
							<th>Modified</th>
							<th>Actions</th>
						</tr>
					</thead>
					<tbody>
						{#each files as file (file.name)}
							<tr class:selected={selectedFile === file.name}>
								<td>
									<button
										class="file-name"
										onclick={() => navigateTo(file)}
										title={file.isDirectory ? 'Open folder' : 'Open file'}
									>
										{file.isDirectory ? '📁' : '📄'} {file.name}
									</button>
								</td>
								<td>{file.isDirectory ? '-' : formatBytes(file.size)}</td>
								<td class="date">{formatDate(file.modified)}</td>
								<td class="row-actions">
									{#if serverName}
										<a
											class="btn-small icon-button"
											href={fileDownloadUrl(serverName, joinPath(currentPath, file.name))}
											download
											title={file.isDirectory ? 'Download as .zip' : 'Download'}
											aria-label={file.isDirectory ? `Download ${file.name} as .zip` : `Download ${file.name}`}
										>
											<svg viewBox="0 0 24 24" aria-hidden="true">
												<path
													d="M12 4v11M7 10l5 5 5-5M5 20h14"
													fill="none"
													stroke="currentColor"
													stroke-width="1.8"
													stroke-linecap="round"
													stroke-linejoin="round"
												/>
											</svg>
										</a>
									{/if}
									<button
										onclick={() => openDialog('move', file)}
										class="btn-small icon-button"
										title="Rename / move"
										aria-label={`Rename or move ${file.name}`}
									>
										<svg viewBox="0 0 24 24" aria-hidden="true">
											<path
												d="M4 20h4l10-10-4-4L4 16v4zM14 6l4 4"
												fill="none"
												stroke="currentColor"
												stroke-width="1.8"
												stroke-linecap="round"
												stroke-linejoin="round"
											/>
										</svg>
									</button>
									<button
										onclick={() => openDialog('copy', file)}
										class="btn-small icon-button"
										title="Copy"
										aria-label={`Copy ${file.name}`}
									>
										<svg viewBox="0 0 24 24" aria-hidden="true">
											<path
												d="M9 9h10v11H9zM5 15V4h10"
												fill="none"
												stroke="currentColor"
												stroke-width="1.8"
												stroke-linecap="round"
												stroke-linejoin="round"
											/>
										</svg>
									</button>
									<button
										onclick={() => deleteFile(file)}
										class="btn-small btn-danger icon-button"
										title={file.isDirectory ? 'Delete folder' : 'Delete file'}
										aria-label={`Delete ${file.name}`}
									>
										<svg viewBox="0 0 24 24" aria-hidden="true">
											<path
												d="M4 7h16M9 7V5h6v2M10 11v6M14 11v6M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12"
												fill="none"
												stroke="currentColor"
												stroke-width="1.8"
												stroke-linecap="round"
												stroke-linejoin="round"
											/>
										</svg>
									</button>
								</td>
							</tr>
						{/each}
					</tbody>
				</table>
				</div>
			{/if}
		</div>

		{#if selectedFile}
			<div class="file-viewer">
				<div class="viewer-toolbar">
					<h3>
						{selectedFile}
						{#if isDirty}
							<span class="dirty-badge" title="Unsaved changes">● unsaved</span>
						{/if}
					</h3>
					<div class="viewer-actions">
						{#if serverName}
							<a
								class="btn-small icon-button"
								href={fileDownloadUrl(serverName, joinPath(currentPath, selectedFile))}
								download
								title="Download"
								aria-label={`Download ${selectedFile}`}
							>
								<svg viewBox="0 0 24 24" aria-hidden="true">
									<path
										d="M12 4v11M7 10l5 5 5-5M5 20h14"
										fill="none"
										stroke="currentColor"
										stroke-width="1.8"
										stroke-linecap="round"
										stroke-linejoin="round"
									/>
								</svg>
							</a>
						{/if}
						{#if editMode}
							<button onclick={cancelEdit} class="btn-small">Cancel</button>
							<button onclick={saveFile} class="btn-small btn-primary" disabled={loading}>
								Save
							</button>
						{:else}
							<button
								onclick={() => (editMode = true)}
								class="btn-small btn-primary icon-button"
								title="Edit file"
								aria-label="Edit file"
							>
								<svg viewBox="0 0 24 24" aria-hidden="true">
									<path
										d="M4 20h4l10-10-4-4L4 16v4zM14 6l4 4"
										fill="none"
										stroke="currentColor"
										stroke-width="1.8"
										stroke-linecap="round"
										stroke-linejoin="round"
									/>
								</svg>
							</button>
						{/if}
					</div>
				</div>
				<textarea
					bind:value={fileContent}
					readonly={!editMode}
					class="file-content"
					class:editable={editMode}
					onkeydown={onEditorKeydown}
					spellcheck="false"
				></textarea>
				{#if editMode}
					<p class="editor-hint">Ctrl+S saves.</p>
				{/if}
			</div>
		{/if}
	</div>
</div>

{#if dialog}
	<div class="dialog-backdrop" role="presentation" onclick={(e) => e.target === e.currentTarget && !dialog?.busy && (dialog = null)}>
		<form class="dialog" onsubmit={submitDialog} aria-labelledby="file-dialog-title">
			<h3 id="file-dialog-title">
				{dialogTitles[dialog.kind]}{dialog.source ? ` "${dialog.source.name}"` : ''}
			</h3>
			<label>
				<span class="label-text">
					{#if dialog.kind === 'new-file' || dialog.kind === 'new-folder'}
						Name (in {currentPath})
					{:else}
						New path (from the server folder)
					{/if}
				</span>
				<!-- svelte-ignore a11y_autofocus -->
				<input
					type="text"
					bind:value={dialog.value}
					spellcheck="false"
					autofocus
					placeholder={dialog.kind === 'new-folder' ? 'my-folder' : dialog.kind === 'new-file' ? 'config.yml' : ''}
				/>
			</label>
			{#if (dialog.kind === 'move' || dialog.kind === 'copy') && !dialog.source?.isDirectory}
				<label class="checkbox">
					<input type="checkbox" bind:checked={dialog.overwrite} />
					<span>Replace an existing file at that path</span>
				</label>
			{/if}
			{#if dialog.error}
				<p class="error">{dialog.error}</p>
			{/if}
			<div class="dialog-actions">
				<button type="button" class="btn" onclick={() => (dialog = null)} disabled={dialog.busy}>
					Cancel
				</button>
				<button type="submit" class="btn btn-primary" disabled={dialog.busy}>
					{dialog.busy ? 'Working...' : dialogTitles[dialog.kind]}
				</button>
			</div>
		</form>
	</div>
{/if}

<style>
	.file-browser {
		display: flex;
		flex-direction: column;
		gap: 1rem;
		height: 100%;
	}

	.toolbar {
		display: flex;
		justify-content: space-between;
		align-items: center;
		padding: 1rem;
		background: var(--mc-panel-dark, #141827);
		border: 1px solid var(--border-color, #2a2f47);
		border-radius: 8px;
	}

	.toolbar-actions {
		display: flex;
		gap: 0.5rem;
		align-items: center;
	}

	.breadcrumb {
		display: flex;
		align-items: center;
		gap: 0.5rem;
	}

	.path {
		font-family: 'Courier New', monospace;
		color: var(--mc-text-muted, #9aa2c5);
	}

	.content {
		display: grid;
		grid-template-columns: 1fr 1fr;
		gap: 1rem;
		flex: 1;
		overflow: hidden;
	}

	.file-list {
		background: var(--mc-panel-dark, #141827);
		border: 1px solid var(--border-color, #2a2f47);
		border-radius: 8px;
		padding: 1.25rem;
		overflow: auto;
		scrollbar-width: thin;
		scrollbar-color: var(--mc-panel-lighter, #3a3f5a) transparent;
	}

	.file-viewer {
		background: var(--mc-panel-dark, #141827);
		border: 1px solid var(--border-color, #2a2f47);
		border-radius: 8px;
		padding: 1.25rem 1.25rem 1rem 1.25rem;
		overflow: hidden;
		display: flex;
		flex-direction: column;
	}

	.file-list::-webkit-scrollbar {
		width: 8px;
		height: 8px;
	}

	.file-list::-webkit-scrollbar-track {
		background: transparent;
	}

	.file-list::-webkit-scrollbar-thumb {
		background: var(--mc-panel-lighter, #3a3f5a);
		border-radius: 999px;
	}

	.file-list::-webkit-scrollbar-thumb:hover {
		background: var(--mc-grass, #6ab04c);
	}

	.file-list h3,
	.file-viewer h3 {
		margin-top: 0;
		color: var(--mc-text, #eef0f8);
	}

	.table-scroll {
		overflow-x: auto;
	}

	table {
		width: 100%;
		min-width: 480px;
		border-collapse: collapse;
	}

	thead {
		background: var(--mc-panel, #1a1e2f);
		position: sticky;
		top: 0;
	}

	th,
	td {
		padding: 0.5rem;
		text-align: left;
		border-bottom: 1px solid var(--border-color, #2a2f47);
	}

	th {
		font-weight: 600;
		color: var(--mc-text-muted, #9aa2c5);
	}

	tr:hover {
		background: var(--mc-panel-light, #2a2f47);
	}

	tr.selected {
		background: rgba(106, 176, 76, 0.15);
		border-left: 2px solid var(--mc-grass, #6ab04c);
	}

	.file-name {
		background: none;
		border: none;
		color: var(--color-info, #5b9eff);
		cursor: pointer;
		padding: 0;
		font-size: 1rem;
		text-align: left;
		transition: color 0.2s;
	}

	.file-name:hover {
		color: var(--color-info-light, #a5b4fc);
		text-decoration: underline;
	}

	.empty {
		color: var(--mc-text-dim, #7c87b2);
		text-align: center;
		padding: 2rem;
	}

	.error {
		color: var(--color-error-light, #ff9f9f);
		font-size: 0.85rem;
		margin-bottom: 0.5rem;
		padding: 0.5rem;
		background: var(--color-error-bg, rgba(255, 92, 92, 0.15));
		border: 1px solid var(--color-error-border, rgba(255, 92, 92, 0.3));
		border-radius: 4px;
	}

	.viewer-toolbar {
		display: flex;
		justify-content: space-between;
		align-items: center;
		margin-bottom: 1rem;
		flex-shrink: 0;
	}

	.file-content {
		width: 100%;
		flex: 1;
		min-height: 0;
		background: var(--mc-panel-darkest, #0d1117);
		color: var(--mc-text-secondary, #c4cff5);
		border: 1px solid var(--border-color, #2a2f47);
		border-radius: 8px;
		padding: 1rem;
		font-family: 'Courier New', 'Consolas', monospace;
		font-size: 0.9rem;
		line-height: 1.5;
		resize: none;
		transition: border-color 0.2s, box-shadow 0.2s;
		scrollbar-width: thin;
		scrollbar-color: var(--mc-panel-lighter, #3a3f5a) var(--mc-panel, #1a1e2f);
	}

	.file-content::-webkit-scrollbar {
		width: 8px;
		height: 8px;
	}

	.file-content::-webkit-scrollbar-track {
		background: var(--mc-panel, #1a1e2f);
		border-radius: 4px;
	}

	.file-content::-webkit-scrollbar-thumb {
		background: var(--mc-panel-lighter, #3a3f5a);
		border-radius: 4px;
	}

	.file-content::-webkit-scrollbar-thumb:hover {
		background: var(--mc-grass, #6ab04c);
	}

	.file-content.editable {
		border-color: var(--mc-grass, #6ab04c);
		box-shadow: 0 0 0 2px rgba(106, 176, 76, 0.1);
	}

	.btn,
	.btn-small {
		background: var(--mc-panel-light, #2a2f47);
		color: var(--mc-text, #eef0f8);
		border: 1px solid var(--border-color, #2a2f47);
		padding: 0.5rem 1rem;
		border-radius: 8px;
		cursor: pointer;
		font-size: 0.9rem;
		transition: all 0.2s;
		font-family: inherit;
	}

	.btn-small {
		padding: 0.4rem 0.75rem;
		font-size: 0.85rem;
	}

	.icon-button {
		display: inline-flex;
		align-items: center;
		justify-content: center;
		gap: 0;
		line-height: 0;
		padding: 0.4rem;
	}

	.icon-button svg {
		width: 14px;
		height: 14px;
	}

	.btn:hover,
	.btn-small:hover {
		background: var(--mc-panel-lighter, #3a3f5a);
		border-color: var(--mc-panel-lighter, #3a3f5a);
	}

	.btn:disabled,
	.btn-small:disabled {
		opacity: 0.5;
		cursor: not-allowed;
	}

	.btn-icon {
		background: none;
		border: none;
		color: var(--color-info, #5b9eff);
		cursor: pointer;
		font-size: 1rem;
		padding: 0.4rem 0.75rem;
		transition: color 0.2s;
	}

	.btn-icon:hover:not(:disabled) {
		color: var(--color-info-light, #a5b4fc);
	}

	.btn-icon:disabled {
		color: var(--mc-text-dim, #7c87b2);
		cursor: not-allowed;
	}

	.btn-primary {
		background: var(--mc-grass, #6ab04c);
		border-color: var(--mc-grass, #6ab04c);
		color: #fff;
	}

	.btn-primary:hover:not(:disabled) {
		background: var(--mc-grass-dark, #4a8b34);
		border-color: var(--mc-grass-dark, #4a8b34);
	}

	.btn-danger {
		background: var(--color-error, #ff6b6b);
		border-color: var(--color-error, #ff6b6b);
		color: #fff;
	}

	.btn-danger:hover {
		background: var(--color-error-light, #ff9f9f);
		border-color: var(--color-error-light, #ff9f9f);
	}

	.upload-btn {
		display: inline-flex;
		align-items: center;
	}

	a.btn,
	a.btn-small {
		text-decoration: none;
		display: inline-flex;
		align-items: center;
	}

	.toolbar-actions {
		flex-wrap: wrap;
	}

	.row-actions {
		white-space: nowrap;
	}

	.row-actions > * + * {
		margin-left: 0.2rem;
	}

	td.date {
		font-size: 0.85rem;
		color: var(--mc-text-muted, #9aa2c5);
	}

	.viewer-actions {
		display: flex;
		gap: 0.4rem;
		align-items: center;
	}

	.dirty-badge {
		margin-left: 0.5rem;
		font-size: 0.75rem;
		font-weight: 600;
		color: var(--color-warning, #ffd54f);
		vertical-align: middle;
	}

	.editor-hint {
		margin: 0.4rem 0 0;
		font-size: 0.75rem;
		color: var(--mc-text-dim, #7c87b2);
	}

	.dialog-backdrop {
		position: fixed;
		inset: 0;
		background: rgba(5, 7, 15, 0.65);
		display: flex;
		align-items: center;
		justify-content: center;
		padding: 16px;
		z-index: 1000;
	}

	.dialog {
		width: min(460px, 100%);
		background: var(--mc-panel, #1a1e2f);
		border: 1px solid var(--border-color, #2a2f47);
		border-radius: 12px;
		padding: 1.25rem;
		display: flex;
		flex-direction: column;
		gap: 0.9rem;
		box-shadow: 0 20px 40px rgba(0, 0, 0, 0.45);
	}

	.dialog h3 {
		margin: 0;
		color: var(--mc-text, #eef0f8);
		overflow-wrap: anywhere;
	}

	.dialog label {
		display: flex;
		flex-direction: column;
		gap: 0.4rem;
	}

	.dialog .label-text {
		font-size: 0.8rem;
		color: var(--mc-text-muted, #9aa2c5);
	}

	.dialog input[type='text'] {
		background: var(--mc-panel-darkest, #0d1117);
		border: 1px solid var(--border-color, #2a2f47);
		border-radius: 8px;
		padding: 0.6rem 0.75rem;
		color: var(--mc-text, #eef0f8);
		font-family: 'Courier New', 'Consolas', monospace;
		font-size: 0.9rem;
	}

	.dialog input[type='text']:focus {
		outline: none;
		border-color: var(--mc-grass, #6ab04c);
	}

	.dialog .checkbox {
		flex-direction: row;
		align-items: center;
		gap: 0.5rem;
		font-size: 0.85rem;
		color: var(--mc-text-muted, #9aa2c5);
	}

	.dialog-actions {
		display: flex;
		justify-content: flex-end;
		gap: 0.5rem;
	}

	@media (max-width: 900px) {
		.content {
			grid-template-columns: 1fr;
		}

		.toolbar {
			flex-direction: column;
			align-items: stretch;
			gap: 0.75rem;
		}
	}
</style>
