# Discussion: DeploymentRequested

**Status: open.** Suggested reviewers: Polina and Vlad. Related task: [#2](https://github.com/project-cloud-switch/CloudSwitch/issues/2).

## Current gap

API and Doctor will publish the same request. Its current specification has only name, image, environment and provider; it cannot describe the agreed migration.

## Decide

- Which source deployment and target configuration references are required?
- What must be captured in the immutable snapshot: image digest, port/resources, application settings, secret references, approved Terraform module/revision, target health policy and traffic configuration?
- How do we distinguish a user request from automatic recovery, and link an automatic job to its incident?
- Which values must be embedded versus resolved from stored configuration? Referenced values must remain reproducible after configuration edits.

## Keep fixed

API and Doctor save the job before publishing through an outbox. Source access is not required during migration. No credentials, arbitrary HCL or shell commands belong in the request.

## Meeting output

An agreed field/type/requiredness list and sample payloads for manual and automatic migration, plus responsibility for updating the C# contract.
