ALTER TABLE dbo.ToolExecution ADD ExpiresUtc datetime2(3) NULL;
GO
ALTER TABLE dbo.ToolExecution ADD ResultContent nvarchar(max) NULL;
GO
ALTER TABLE dbo.ToolExecution ADD ResultIsError bit NULL;
GO
CREATE INDEX IX_ToolExecution_Pending ON dbo.ToolExecution(ConversationId) WHERE Status = 'PendingApproval';
GO
ALTER TABLE dbo.VisitorSession ADD ExternalSessionId uniqueidentifier NULL;
GO
ALTER TABLE dbo.Conversation ADD ExternalSessionId uniqueidentifier NULL;
GO
CREATE INDEX IX_Conversation_Agent_ExternalSession ON dbo.Conversation(AgentId, ExternalSessionId) WHERE ExternalSessionId IS NOT NULL;
GO
ALTER TABLE dbo.Agent ADD AllowExternalSessionId bit NOT NULL CONSTRAINT DF_Agent_AllowExternalSessionId DEFAULT 1;
GO
