CREATE TABLE dbo.VisitorSession (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_VisitorSession PRIMARY KEY,
    AccountId uniqueidentifier NOT NULL,
    AgentId uniqueidentifier NOT NULL,
    TokenHash binary(32) NOT NULL,
    Mode nvarchar(10) NOT NULL,
    ExternalUserId nvarchar(200) NULL,
    ExternalDisplayName nvarchar(200) NULL,
    HostOrigin nvarchar(300) NOT NULL,
    IpHash binary(32) NOT NULL,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_VisitorSession_Created DEFAULT SYSUTCDATETIME(),
    LastSeenUtc datetime2(3) NOT NULL CONSTRAINT DF_VisitorSession_LastSeen DEFAULT SYSUTCDATETIME(),
    ExpiresUtc datetime2(3) NOT NULL,
    RevokedUtc datetime2(3) NULL,
    CONSTRAINT UQ_VisitorSession_Token UNIQUE (TokenHash),
    CONSTRAINT CK_VisitorSession_Mode CHECK (Mode IN ('Public','Signed','Preview')),
    CONSTRAINT FK_VisitorSession_Agent FOREIGN KEY (AccountId, AgentId) REFERENCES dbo.Agent(AccountId, Id) ON DELETE CASCADE
);
GO
CREATE INDEX IX_VisitorSession_Agent_External ON dbo.VisitorSession(AgentId, ExternalUserId);
GO
CREATE TABLE dbo.Conversation (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_Conversation PRIMARY KEY,
    AccountId uniqueidentifier NOT NULL,
    AgentId uniqueidentifier NOT NULL,
    VisitorSessionId uniqueidentifier NOT NULL,
    ExternalUserId nvarchar(200) NULL,
    Title nvarchar(200) NOT NULL CONSTRAINT DF_Conversation_Title DEFAULT N'',
    ActiveTurnId uniqueidentifier NULL,
    ActiveTurnExpiresUtc datetime2(3) NULL,
    MessageCount int NOT NULL CONSTRAINT DF_Conversation_Count DEFAULT 0,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Conversation_Created DEFAULT SYSUTCDATETIME(),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Conversation_Updated DEFAULT SYSUTCDATETIME(),
    DeletedUtc datetime2(3) NULL,
    CONSTRAINT UQ_Conversation_AccountId_Id UNIQUE (AccountId, Id),
    CONSTRAINT FK_Conversation_Agent FOREIGN KEY (AccountId, AgentId) REFERENCES dbo.Agent(AccountId, Id) ON DELETE CASCADE
);
GO
CREATE INDEX IX_Conversation_Agent_Session ON dbo.Conversation(AgentId, VisitorSessionId, UpdatedUtc DESC);
GO
CREATE INDEX IX_Conversation_Agent_External ON dbo.Conversation(AgentId, ExternalUserId, UpdatedUtc DESC);
GO
CREATE TABLE dbo.ConversationMessage (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_ConversationMessage PRIMARY KEY,
    AccountId uniqueidentifier NOT NULL,
    ConversationId uniqueidentifier NOT NULL,
    Sequence int NOT NULL,
    TurnId uniqueidentifier NOT NULL,
    Role nvarchar(10) NOT NULL,
    ContentJson nvarchar(max) NOT NULL,
    DisplayText nvarchar(max) NOT NULL CONSTRAINT DF_ConversationMessage_Text DEFAULT N'',
    CitationsJson nvarchar(max) NULL,
    ModelId nvarchar(100) NULL,
    ModelFamily nvarchar(10) NULL,
    StopReason nvarchar(30) NULL,
    InputTokens int NOT NULL CONSTRAINT DF_CM_In DEFAULT 0,
    OutputTokens int NOT NULL CONSTRAINT DF_CM_Out DEFAULT 0,
    CacheReadTokens int NOT NULL CONSTRAINT DF_CM_CacheRead DEFAULT 0,
    CacheWriteTokens int NOT NULL CONSTRAINT DF_CM_CacheWrite DEFAULT 0,
    WebSearchRequests int NOT NULL CONSTRAINT DF_CM_Search DEFAULT 0,
    IsIncomplete bit NOT NULL CONSTRAINT DF_CM_Incomplete DEFAULT 0,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_ConversationMessage_Created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_ConversationMessage_Seq UNIQUE (ConversationId, Sequence),
    CONSTRAINT CK_ConversationMessage_Role CHECK (Role IN ('user','assistant')),
    CONSTRAINT CK_ConversationMessage_Content CHECK (ISJSON(ContentJson) = 1),
    CONSTRAINT FK_ConversationMessage_Conversation FOREIGN KEY (AccountId, ConversationId) REFERENCES dbo.Conversation(AccountId, Id) ON DELETE CASCADE
);
GO
CREATE TABLE dbo.UsageDaily (
    AccountId uniqueidentifier NOT NULL,
    AgentId uniqueidentifier NOT NULL,
    DateUtc date NOT NULL,
    Turns int NOT NULL CONSTRAINT DF_UsageDaily_Turns DEFAULT 0,
    InputTokens bigint NOT NULL CONSTRAINT DF_UsageDaily_In DEFAULT 0,
    OutputTokens bigint NOT NULL CONSTRAINT DF_UsageDaily_Out DEFAULT 0,
    WebSearches int NOT NULL CONSTRAINT DF_UsageDaily_Search DEFAULT 0,
    ToolCalls int NOT NULL CONSTRAINT DF_UsageDaily_Tools DEFAULT 0,
    RejectedRequests int NOT NULL CONSTRAINT DF_UsageDaily_Rejected DEFAULT 0,
    CONSTRAINT PK_UsageDaily PRIMARY KEY (AgentId, DateUtc),
    CONSTRAINT FK_UsageDaily_Agent FOREIGN KEY (AccountId, AgentId) REFERENCES dbo.Agent(AccountId, Id) ON DELETE CASCADE
);
GO
