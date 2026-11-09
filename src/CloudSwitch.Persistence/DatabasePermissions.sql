-- Run once per database as the schema owner, after migrations. Grant these NOLOGIN roles
-- to separately provisioned API/Worker logins; passwords are supplied by the deployment.
DO $$ BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'cloudswitch_api') THEN
        CREATE ROLE cloudswitch_api NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'cloudswitch_worker') THEN
        CREATE ROLE cloudswitch_worker NOLOGIN;
    END IF;
END $$;

GRANT USAGE ON SCHEMA public TO cloudswitch_api, cloudswitch_worker;
GRANT SELECT, INSERT ON "Users" TO cloudswitch_api;
GRANT UPDATE ("Username") ON "Users" TO cloudswitch_api;
GRANT SELECT, INSERT ON "Deployments", "Targets" TO cloudswitch_api;
GRANT UPDATE ("Name", "Revision", "Definition", "IsDeleted", "UpdatedAt") ON "Deployments" TO cloudswitch_api;
GRANT UPDATE ("Revision", "Configuration", "IsDeleted", "UpdatedAt") ON "Targets" TO cloudswitch_api;
GRANT SELECT, INSERT ON "Jobs" TO cloudswitch_api, cloudswitch_worker;
GRANT SELECT ON "Runtimes", "Health", "Incidents", "JobEvents" TO cloudswitch_api;
GRANT SELECT ON "Users", "Deployments", "Targets" TO cloudswitch_worker;
GRANT SELECT, INSERT, UPDATE ON "Runtimes", "Health" TO cloudswitch_worker;
GRANT SELECT, INSERT ON "Incidents", "JobEvents" TO cloudswitch_worker;
GRANT UPDATE ("ResolvedAt") ON "Incidents" TO cloudswitch_worker;
GRANT UPDATE ("Status", "Phase", "Attempt", "Sequence", "LeaseOwner", "LeaseExpiresAt", "FencingToken",
    "Checkpoints", "TargetEndpoint", "Cutover", "ErrorCode", "ErrorMessage", "UpdatedAt") ON "Jobs" TO cloudswitch_worker;
GRANT SELECT, INSERT, UPDATE, DELETE ON "InboxState", "OutboxState", "OutboxMessage" TO cloudswitch_api, cloudswitch_worker;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO cloudswitch_api, cloudswitch_worker;
