CREATE TABLE dbo.Agent (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_Agent PRIMARY KEY,
    AccountId uniqueidentifier NOT NULL,
    Name nvarchar(200) NOT NULL,
    Description nvarchar(1000) NULL,
    SystemPrompt nvarchar(max) NOT NULL CONSTRAINT DF_Agent_SystemPrompt DEFAULT N'',
    DefaultModelFamily nvarchar(10) NOT NULL CONSTRAINT DF_Agent_Family DEFAULT 'sonnet',
    PinnedModelId nvarchar(100) NULL,
    ShowModelSelector bit NOT NULL CONSTRAINT DF_Agent_ShowSelector DEFAULT 0,
    AllowedModelFamilies nvarchar(30) NOT NULL CONSTRAINT DF_Agent_Allowed DEFAULT 'haiku,sonnet,opus',
    MaxOutputTokens int NOT NULL CONSTRAINT DF_Agent_MaxOut DEFAULT 4096,
    WebSearchEnabled bit NOT NULL CONSTRAINT DF_Agent_WebSearch DEFAULT 0,
    WebSearchMaxUses int NOT NULL CONSTRAINT DF_Agent_WebSearchMax DEFAULT 5,
    WebSearchDomainMode nvarchar(10) NOT NULL CONSTRAINT DF_Agent_WebSearchMode DEFAULT 'None',
    WebSearchDomainsJson nvarchar(max) NULL,
    IdentityMode nvarchar(20) NOT NULL CONSTRAINT DF_Agent_Identity DEFAULT 'Public',
    AppearanceJson nvarchar(max) NOT NULL CONSTRAINT DF_Agent_Appearance DEFAULT N'{}',
    LimitsJson nvarchar(max) NULL,
    Status nvarchar(10) NOT NULL CONSTRAINT DF_Agent_Status DEFAULT 'Disabled',
    ConcurrencyStamp rowversion NOT NULL,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Agent_Created DEFAULT SYSUTCDATETIME(),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Agent_Updated DEFAULT SYSUTCDATETIME(),
    DeletedUtc datetime2(3) NULL,
    CONSTRAINT UQ_Agent_AccountId_Id UNIQUE (AccountId, Id),
    CONSTRAINT FK_Agent_Account FOREIGN KEY (AccountId) REFERENCES dbo.Account(Id) ON DELETE CASCADE,
    CONSTRAINT CK_Agent_Family CHECK (DefaultModelFamily IN ('haiku','sonnet','opus')),
    CONSTRAINT CK_Agent_WebSearchMode CHECK (WebSearchDomainMode IN ('None','Allow','Block')),
    CONSTRAINT CK_Agent_Identity CHECK (IdentityMode IN ('Public','SignedOptional','SignedRequired')),
    CONSTRAINT CK_Agent_Status CHECK (Status IN ('Enabled','Disabled')),
    CONSTRAINT CK_Agent_Appearance CHECK (ISJSON(AppearanceJson) = 1),
    CONSTRAINT CK_Agent_Limits CHECK (LimitsJson IS NULL OR ISJSON(LimitsJson) = 1),
    CONSTRAINT CK_Agent_Domains CHECK (WebSearchDomainsJson IS NULL OR ISJSON(WebSearchDomainsJson) = 1)
);
GO
CREATE INDEX IX_Agent_Account_Deleted ON dbo.Agent(AccountId, DeletedUtc);
GO
CREATE TABLE dbo.AgentAllowedOrigin (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_AgentAllowedOrigin PRIMARY KEY,
    AccountId uniqueidentifier NOT NULL,
    AgentId uniqueidentifier NOT NULL,
    Origin nvarchar(300) NOT NULL,
    CONSTRAINT UQ_AgentAllowedOrigin UNIQUE (AgentId, Origin),
    CONSTRAINT FK_AgentAllowedOrigin_Agent FOREIGN KEY (AccountId, AgentId) REFERENCES dbo.Agent(AccountId, Id) ON DELETE CASCADE
);
GO
