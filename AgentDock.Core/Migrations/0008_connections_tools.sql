CREATE TABLE dbo.[Connection] (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_Connection PRIMARY KEY,
    AccountId uniqueidentifier NOT NULL,
    Name nvarchar(100) NOT NULL,
    Kind nvarchar(10) NOT NULL,
    BaseUrl nvarchar(2048) NOT NULL,
    AuthType nvarchar(20) NOT NULL CONSTRAINT DF_Connection_AuthType DEFAULT 'None',
    AuthHeaderName nvarchar(100) NULL,
    SecretId uniqueidentifier NULL,
    StaticHeadersJson nvarchar(max) NULL,
    TimeoutSeconds int NOT NULL CONSTRAINT DF_Connection_Timeout DEFAULT 30,
    Status nvarchar(10) NOT NULL CONSTRAINT DF_Connection_Status DEFAULT 'Untested',
    LastTestedUtc datetime2(3) NULL,
    LastError nvarchar(1000) NULL,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Connection_Created DEFAULT SYSUTCDATETIME(),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Connection_Updated DEFAULT SYSUTCDATETIME(),
    DeletedUtc datetime2(3) NULL,
    CONSTRAINT UQ_Connection_AccountId_Id UNIQUE (AccountId, Id),
    CONSTRAINT FK_Connection_Account FOREIGN KEY (AccountId) REFERENCES dbo.Account(Id) ON DELETE CASCADE,
    CONSTRAINT FK_Connection_Secret FOREIGN KEY (SecretId) REFERENCES dbo.Secret(Id),
    CONSTRAINT CK_Connection_Kind CHECK (Kind IN ('Mcp','Http')),
    CONSTRAINT CK_Connection_Auth CHECK (AuthType IN ('None','Bearer','Header')),
    CONSTRAINT CK_Connection_Status CHECK (Status IN ('Untested','Ok','Error')),
    CONSTRAINT CK_Connection_Headers CHECK (StaticHeadersJson IS NULL OR ISJSON(StaticHeadersJson) = 1)
);
GO
CREATE UNIQUE INDEX UX_Connection_Account_Name ON dbo.[Connection](AccountId, Name) WHERE DeletedUtc IS NULL;
GO
CREATE TABLE dbo.McpToolSnapshot (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_McpToolSnapshot PRIMARY KEY,
    AccountId uniqueidentifier NOT NULL,
    ConnectionId uniqueidentifier NOT NULL,
    ToolName nvarchar(128) NOT NULL,
    Title nvarchar(200) NULL,
    Description nvarchar(max) NOT NULL CONSTRAINT DF_McpToolSnapshot_Desc DEFAULT N'',
    InputSchemaJson nvarchar(max) NOT NULL,
    AnnotationsJson nvarchar(max) NULL,
    SchemaHash binary(32) NOT NULL,
    IsPresent bit NOT NULL CONSTRAINT DF_McpToolSnapshot_Present DEFAULT 1,
    DiscoveredUtc datetime2(3) NOT NULL CONSTRAINT DF_McpToolSnapshot_Discovered DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_McpToolSnapshot UNIQUE (ConnectionId, ToolName),
    CONSTRAINT FK_McpToolSnapshot_Connection FOREIGN KEY (AccountId, ConnectionId) REFERENCES dbo.[Connection](AccountId, Id) ON DELETE CASCADE,
    CONSTRAINT CK_McpToolSnapshot_Schema CHECK (ISJSON(InputSchemaJson) = 1),
    CONSTRAINT CK_McpToolSnapshot_Annotations CHECK (AnnotationsJson IS NULL OR ISJSON(AnnotationsJson) = 1)
);
GO
CREATE TABLE dbo.AgentConnection (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_AgentConnection PRIMARY KEY,
    AccountId uniqueidentifier NOT NULL,
    AgentId uniqueidentifier NOT NULL,
    ConnectionId uniqueidentifier NOT NULL,
    Alias nvarchar(24) NOT NULL,
    Enabled bit NOT NULL CONSTRAINT DF_AgentConnection_Enabled DEFAULT 1,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_AgentConnection_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_AgentConnection_Agent_Connection UNIQUE (AgentId, ConnectionId),
    CONSTRAINT UQ_AgentConnection_Agent_Alias UNIQUE (AgentId, Alias),
    CONSTRAINT UQ_AgentConnection_AccountId_Id UNIQUE (AccountId, Id),
    CONSTRAINT FK_AgentConnection_Agent FOREIGN KEY (AccountId, AgentId) REFERENCES dbo.Agent(AccountId, Id) ON DELETE CASCADE,
    CONSTRAINT FK_AgentConnection_Connection FOREIGN KEY (AccountId, ConnectionId) REFERENCES dbo.[Connection](AccountId, Id)
);
GO
CREATE TABLE dbo.AgentMcpTool (
    AccountId uniqueidentifier NOT NULL,
    AgentConnectionId uniqueidentifier NOT NULL,
    ToolName nvarchar(128) NOT NULL,
    RequiresConfirmation bit NOT NULL CONSTRAINT DF_AgentMcpTool_Confirm DEFAULT 1,
    ApprovedSchemaHash binary(32) NOT NULL,
    CONSTRAINT PK_AgentMcpTool PRIMARY KEY (AgentConnectionId, ToolName),
    CONSTRAINT FK_AgentMcpTool_AgentConnection FOREIGN KEY (AccountId, AgentConnectionId) REFERENCES dbo.AgentConnection(AccountId, Id) ON DELETE CASCADE
);
GO
CREATE TABLE dbo.ToolExecution (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_ToolExecution PRIMARY KEY,
    AccountId uniqueidentifier NOT NULL,
    AgentId uniqueidentifier NOT NULL,
    ConversationId uniqueidentifier NOT NULL,
    TurnId uniqueidentifier NOT NULL,
    ToolUseId nvarchar(100) NOT NULL,
    ExposedName nvarchar(128) NOT NULL,
    Kind nvarchar(10) NOT NULL,
    ConnectionId uniqueidentifier NULL,
    HttpToolId uniqueidentifier NULL,
    UpstreamToolName nvarchar(128) NULL,
    InputJson nvarchar(max) NULL,
    Status nvarchar(20) NOT NULL,
    ErrorCode nvarchar(60) NULL,
    ErrorMessage nvarchar(1000) NULL,
    HttpStatus int NULL,
    ResultPreview nvarchar(4000) NULL,
    ResultBytes int NULL,
    DurationMs int NULL,
    RequestedUtc datetime2(3) NOT NULL CONSTRAINT DF_ToolExecution_Requested DEFAULT SYSUTCDATETIME(),
    DecidedUtc datetime2(3) NULL,
    StartedUtc datetime2(3) NULL,
    CompletedUtc datetime2(3) NULL,
    CONSTRAINT CK_ToolExecution_Kind CHECK (Kind IN ('Mcp','Http','Unknown')),
    CONSTRAINT CK_ToolExecution_Status CHECK (Status IN ('PendingApproval','Denied','Expired','Blocked','Running','Succeeded','Failed','TimedOut')),
    CONSTRAINT FK_ToolExecution_Conversation FOREIGN KEY (AccountId, ConversationId) REFERENCES dbo.Conversation(AccountId, Id) ON DELETE CASCADE
);
GO
CREATE INDEX IX_ToolExecution_Conversation_Turn ON dbo.ToolExecution(ConversationId, TurnId);
GO
CREATE INDEX IX_ToolExecution_Account_Requested ON dbo.ToolExecution(AccountId, RequestedUtc DESC);
GO
