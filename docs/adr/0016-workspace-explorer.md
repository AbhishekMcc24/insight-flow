# 0016. In-app Workspace Explorer (folders + drag-and-drop uploads)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Deciders:** Roy (tech lead)

## Context

Roy added a requirement: users need a folder structure inside the app, and must be able to drag files and folders from their computer into it.

## Decision

- One content tree. Folders hold uploaded files *and* Insight Flow content (datasets, threads; workbooks/dashboards reserved).
- Two roots: My Workspace (personal, owner only) and Shared (Viewer/Explorer read, Creator/TenantAdmin write), via `IContentPermissionEvaluator`.
- `ItemName` validation, case-insensitive unique names per folder, cycle and depth rules (`FolderTreeRules`), soft delete.
- Upload stores the file first (streaming multipart, SHA-256, size/type limits, `IUploadScanner` seam). "Create dataset" then queues an extract (CSV/Parquet; Excel → 501 until implemented).
- Browser uploads go through the Web's BFF proxy (ADR 0027), one XHR per file with progress. Dropped folders keep their structure through per-file relative paths.

## Consequences

+ The vertical slice starts from a drag-and-drop, as users expect.
- TODO(dev2): trash/restore, sharing, search, versioning, zip download, real virus scanning.
