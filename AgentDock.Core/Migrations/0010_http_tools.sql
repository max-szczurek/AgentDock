CREATE TABLE dbo.HttpTool (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_HttpTool PRIMARY KEY,
    AccountId uniqueidentifier NOT NULL,
    Name nvarchar(64) NOT NULL,
    DisplayName nvarchar(100) NOT NULL,
    Description nvarchar(2000) NOT NULL,
    InputSchemaJson nvarchar(max) NOT NULL,
    ExecutionConfigJson nvarchar(max) NOT NULL,
    ConnectionId uniqueidentifier NOT NULL,
    Version int NOT NULL CONSTRAINT DF_HttpTool_Version DEFAULT 1,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_HttpTool_Created DEFAULT SYSUTCDATETIME(),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_HttpTool_Updated DEFAULT SYSUTCDATETIME(),
    DeletedUtc datetime2(3) NULL,
    CONSTRAINT UQ_HttpTool_AccountId_Id UNIQUE (AccountId, Id),
    CONSTRAINT FK_HttpTool_Account FOREIGN KEY (AccountId) REFERENCES dbo.Account(Id) ON DELETE CASCADE,
    CONSTRAINT FK_HttpTool_Connection FOREIGN KEY (AccountId, ConnectionId) REFERENCES dbo.[Connection](AccountId, Id),
    CONSTRAINT CK_HttpTool_Schema CHECK (ISJSON(InputSchemaJson) = 1),
    CONSTRAINT CK_HttpTool_Config CHECK (ISJSON(ExecutionConfigJson) = 1)
);
GO
CREATE UNIQUE INDEX UX_HttpTool_Account_Name ON dbo.HttpTool(AccountId, Name) WHERE DeletedUtc IS NULL;
GO
CREATE TABLE dbo.AgentHttpTool (
    AccountId uniqueidentifier NOT NULL,
    AgentId uniqueidentifier NOT NULL,
    HttpToolId uniqueidentifier NOT NULL,
    RequiresConfirmation bit NOT NULL CONSTRAINT DF_AgentHttpTool_Confirm DEFAULT 1,
    Enabled bit NOT NULL CONSTRAINT DF_AgentHttpTool_Enabled DEFAULT 1,
    CONSTRAINT PK_AgentHttpTool PRIMARY KEY (AgentId, HttpToolId),
    CONSTRAINT FK_AgentHttpTool_Agent FOREIGN KEY (AccountId, AgentId) REFERENCES dbo.Agent(AccountId, Id) ON DELETE CASCADE,
    CONSTRAINT FK_AgentHttpTool_HttpTool FOREIGN KEY (AccountId, HttpToolId) REFERENCES dbo.HttpTool(AccountId, Id)
);
GO
